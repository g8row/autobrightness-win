# Research: webcam as an ambient light sensor on Windows

Collected 2026-09-22. Our own measurements live in [camera-probe-findings.md](camera-probe-findings.md).

## Prior art

| Project | Platform | Method | Notes |
|---|---|---|---|
| [Clight / Clightd](https://github.com/FedeDP/Clight) | Linux | Captures a few frames at 160x120, applies user-configured V4L2 controls, stops the stream, restores the original controls | Maps brightness through a polynomial fit of user "regression points". Most mature prior art. |
| [wluma](https://github.com/max-baz/wluma) | Linux (wlroots) | Webcam or real ALS, **plus screen content** | Brightens for dark content and dims for bright content, and learns from the user. |
| [ambient-light-detector](https://github.com/elecnix/ambient-light-detector) | Linux | Averages pixels with auto-exposure on, then applies an exponential moving average (EMA) | The author says it measures "screen reflection more than true ambient light", readings are relative only, and the UVC driver got stuck after about 6 days. |
| [cam-auto-brightness](https://github.com/StormTersteeg/cam-auto-brightness), [webcam-als](https://github.com/jug-3866/webcam-als), [auto-brightness](https://github.com/M-y/auto-brightness) | Windows | Pixel averaging | Laptop panels only, no exposure control. |

**Takeaway:** none of the Windows tools lock exposure or calibrate, so their readings are mostly the camera's auto-exposure target. Locking exposure and running a per-camera calibration puts us ahead of all of them.

## Twinkle Tray integration

- **The command pipe we use works on the installed 1.17.2 Store build.** It lives at `\\.\pipe\twinkle-tray\cmds`.
- **Twinkle Tray 1.18 (beta) adds light-sensor support** ([PR #1125](https://github.com/xanderfrangos/twinkle-tray/pull/1125), merged 2026-07-08). The code is in `src/light-sensor/`.
  - **Sources:** Yocto USB hardware, Windows' built-in light-sensor API (the `windows-ambient-sensor` npm package), or a fake sensor for testing.
  - **Mapping:** brightness rises linearly from 0% at a minimum lux value to 100% at a maximum lux value, set per monitor (defaults 5 and 250). It polls every 10 s.
  - **No outside input.** There's no pipe message or file that lets another app supply a lux reading. The PR discussion shows interest in a file-based sensor, but none has been built.
- **Options:**
  1. **Keep driving brightness over the pipe (recommended now).** Our curve and smoothing are richer than Twinkle Tray's linear mapping. Caveat: if the user also enables Twinkle Tray's light sensor, the two will fight, so we should detect that from `settings.json` and warn.
  2. **Send Twinkle Tray an upstream PR** adding an "external" sensor, such as a `{"type":"lux","value":N}` pipe message. Twinkle Tray would then own the mapping and our app would be only the sensor. This is worth offering once our sensor is stable.
  3. **Create a virtual Windows light sensor** (a user-mode sensor driver) that Twinkle Tray's `windows` sensor, and Windows itself, would read. It's the most general option, but it needs a signed driver, so it's out of scope.
- **Monitor wear:** a DDC/CI brightness write may be stored in the monitor's EEPROM, often rated for about 100k writes. [f.lux held back DDC brightness for this reason](https://news.ycombinator.com/item?id=24344696). Our controller must limit writes to a minimum change, a minimum interval and a daily budget.
  - Twinkle Tray's own sensor code writes whenever the rounded percentage changes, with no budget.

## Camera control APIs

- **DirectShow is legacy.** Microsoft recommends Media Foundation and WinRT instead ([docs](https://learn.microsoft.com/en-us/windows/win32/directshow/configure-the-video-quality)).
  - On Windows 11, MediaCapture streams through **Camera Frame Server**, while DirectShow's `IAMCameraControl` opens a separate kernel-streaming handle.
  - Our measurements match this: the camera wedged only in runs that used DirectShow controls, and Frame Server-only runs were reliable.
- **UVC properties through Frame Server:** `VideoDeviceController.Get/SetDevicePropertyByExtendedId`.
  - The request must be the **full 40-byte `KSPROPERTY_VIDEOPROCAMP_S` / `KSPROPERTY_CAMERACONTROL_S` structure**: a 24-byte header, then Value, Flags and Capabilities, padded to 8-byte alignment.
  - Anything shorter returns `MaxPropertyValueSizeTooSmall`. [Others hit the same "data area too small" error](https://learn.microsoft.com/en-us/answers/questions/642621/getting-acces-to-iamvideoprocamp(wmf)-from-uwp(uni)); we found the working layout by trial.
  - Microsoft suggests `IsoSpeedControl` for gain, but UVC cameras usually report it as unsupported. Still to test.
- **Exposure:** `VideoDeviceController.Exposure` (`TrySetAuto`, `TrySetValue`) uses the same log2-seconds units as UVC and works on our camera.
- **Per-frame metadata** ([Capture Stats Metadata](https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/mf-capture-metadata)):
  - Frames can carry `MF_CAPTURE_METADATA_EXPOSURE_TIME` (in 100 ns units), `ISO_SPEED`, `ISO_GAINS` (analog and digital gain as floats) and `HISTOGRAM`.
  - With these, "read back auto-exposure" becomes exact, and we'd never need to change camera settings, so we couldn't disturb other apps.
  - For UVC cameras, metadata is opt-in: it needs UVC 1.5 with the Microsoft extensions plus an INF entry. Old webcams won't have it; newer and laptop cameras might. The calibration wizard should check for it first.

## Coexisting with other camera apps

- `MediaCaptureSharingMode.SharedReadOnly` lets us read frames while another app, such as Teams, owns the camera, but not change settings ([docs](https://learn.microsoft.com/en-us/uwp/api/windows.media.capture.mediacaptureinitializationsettings.sharingmode)). In that case we fall back to reading back auto-exposure, or to metadata.
- **Windows 11 multi-app camera mode:**
  - It's a per-camera toggle in Settings > Bluetooth & devices > Cameras, first available in build 26100.3321, with a wider rollout in [KB5089573 (May–June 2026)](https://www.windowslatest.com/2026/05/30/microsoft-is-killing-the-one-app-at-a-time-camera-limit-in-windows-11-with-new-multi-app-mode/).
  - [When it's on, camera settings like brightness can reportedly only be changed from that Settings page](https://pureinfotech.com/allow-multiple-apps-use-camera-windows-11/). That could block our exposure lock, so we have to detect it and fall back.
  - **Unverified on this machine.**

## Stuck camera firmware

- Linux has a quirk for Logitech B910/C910 cameras whose firmware only [produces invalid frames after USB autosuspend until the streaming interface is reset](https://lkml.iu.edu/hypermail/linux/kernel/2301.0/02611.html). Old Logitech firmware getting stuck is a known pattern.
- On Windows, recovery means a replug or `pnputil /restart-device`, which needs admin. The app must:
  - detect a stream that delivers zero frames and stop hammering the camera;
  - avoid settings that trigger the hang (exposures longer than one frame period, and DirectShow access while streaming);
  - tell the user when a replug is needed.

## Turning pixel values into light levels

- **The photographic relationship** ([EV](https://en.wikipedia.org/wiki/Exposure_value), [camera as lux meter](https://www.conservationphysics.org/lightmtr/luxmtr1.html)): `lux ≈ C·N² / (t·S)` for a mid-grey reading, with calibration constant C ≈ 250.
- **For a webcam** the aperture N is fixed, so `luminance ∝ Y_linear / (t · gain)`. That makes `log2(Y_linear) − log2(t) − log2(gain)` a relative exposure value (EV).
  - **Y_linear:** the pixel value converted back to linear light using the camera's response curve.
  - **t:** exposure time in seconds.
  - **gain:** the camera's amplification.
- **Getting Y_linear right is the calibration job.** Our sweep showed the camera's response isn't a simple gamma 2.2 curve. The standard technique is to recover the response curve from an exposure stack (Debevec & Malik).
  - An absolute lux figure is optional: the user can anchor it once with a phone lux-meter app.
- **Where the camera looks matters.** Measuring a patch of wall or ceiling beats the whole frame, which includes the user's face and screen reflections. Subtracting the screen's own contribution needs the feedback coefficient, measured by stepping monitor brightness (which our `feedback` command does).

## Brightness curve and learning

- Android's adaptive brightness learns from slider moves, but the learned curve can't be inspected ([overview](https://www.androidauthority.com/adaptive-brightness-android-explained-3222961/)).
  - "Glass-box" alternatives show and fit the curve deterministically: [AdvancedAutoBrightness](https://github.com/faded-penguin021/AdvancedAutoBrightness), and Clight's regression points.
  - [kylecorry31/AdaptiveBrightness](https://github.com/kylecorry31/AdaptiveBrightness) uses a small neural network.
- **Recommendation:** an editable curve of light level (EV) to brightness %.
  - Manual changes made in Twinkle Tray add points to it (detected by polling `get`), and the app fits a smooth, always-increasing curve through them.
  - The UI shows the learned points and lets the user remove them.
