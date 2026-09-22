# CameraProbe findings

Measured 2026-09-22 with `spikes/CameraProbe` on Windows 11 (build 26100). Background research is in [research.md](research.md).

## Hardware

| Camera | UVC controls | Usable as a light meter |
|---|---|---|
| Logitech USB Video Device `046D:0819` (C210 family), on a Genesys `05E3:0610` USB 2.0 hub | Exposure −13…0 (log2 s, auto and manual), Gain 0–255 (manual), White balance 0–10000 (auto and manual), Backlight compensation 0/1, Brightness, Contrast, Saturation, Sharpness, Power-line frequency (read-only, set to 50 Hz), Auto-exposure priority (read-only, 1) | **Yes** |
| Camo (virtual phone camera) | None | No: exposure is controlled on the phone |
| OBS Virtual Camera, AMD Privacy View | None | No |

The Logitech offers 162 formats. We use **YUY2 160x120 @ 30 fps**, which is uncompressed and costs negligible CPU.

## Metering with exposure locked (gain 0, white balance manual, backlight compensation off)

Exposure sweep, same scene:

| Exposure | Time | Mean luma | Clipped high | fps |
|---|---|---|---|---|
| −13 | 1/8192 s | 4.0 | 0% | 26 |
| −10 | 1/1024 s | 6.1 | 0% | 24 |
| −8 | 1/256 s | 15.2 | 0% | 24 |
| −7 | 1/128 s | 28.2 | 0% | 24 |
| −6 | 1/64 s | 49.5 | 0% | 24 |
| −5 | 1/32 s | 80.0 | 0% | 24 |
| −4 | 1/16 s | 119.8 | 15.9% | **13** |
| −3 | 1/8 s | 153.9 | 31.9% | **6.6** |
| −2 | 1/4 s | – | – | **stream hung** |

- **Response is monotonic.** There's a black pedestal of about 4 levels, and the tone curve is softer than gamma 2.2 (each exposure doubling raised luma about 1.6–1.9×). A per-camera response calibration is required.
- **Noise at fixed exposure:** frame-to-frame standard deviation of 0.55 levels on a mean of 49 (about 1%).
- **Exposures longer than one frame period (1/32 s at 30 fps) cut the frame rate.** The first attempt at 1/4 s hung the camera. The app caps exposure at −5 and uses gain for darker scenes.
- **Auto-exposure read-back works:** in auto mode the camera reports the exposure and gain it picked (for example exposure −5 with gain 33, or −4 with gain 1).

## Stream reliability

| Scenario | Result |
|---|---|
| First stream after a replug (15 s) | Steady 30 fps |
| Controls changed through DirectShow `IAMCameraControl`/`IAMVideoProcAmp` during and around streaming, then reopened | **Every later open delivered zero frames**, across processes, until a USB replug. Frame Server logs showed no errors. |
| Open/stream/close 5× with no control changes | 5/5 OK: open about 120 ms, first frame about 910 ms, 29.7 fps |
| Open/stream/close 5× with exposure locked through `VideoDeviceController.Exposure` (Frame Server) | 5/5 OK: exposure read back −6, manual; mean 47.6–48.0 every cycle |

**Conclusion:** use Frame Server APIs only. No DirectShow in the app.

## UVC properties through Frame Server

- `VideoDeviceController.Exposure` handles exposure (auto on/off and value).
- The other image-adjustment settings (gain, brightness, and so on) use `Get/SetDevicePropertyByExtendedId` with the full 40-byte structure.
  - **GET is verified:** brightness read back 128, matching DirectShow.
  - **SET is not yet verified.** The first attempt used a shorter request and failed with `UnknownFailure`.

## Open questions

1. Does SET for gain work with the 40-byte structure, and does gain visibly change the luma?
2. Does locking exposure through Frame Server still work with Windows 11 multi-app camera mode switched on?
3. Long-run stability: hours of duty-cycled opens (every 15–60 s).
4. Does this camera provide per-frame capture metadata (exposure time, ISO gains)? Almost certainly not on this model.

## Core library on hardware (abctl)

Calibration of the Logitech `046D:0819` at night (dim room):

- Exposure range 1/8192 s to 1/32 s (capped at the 30 fps frame period); settle time 5 frames.
- **Gain through Frame Server works:** writes are confirmed by read-back, and the range 0–255 is read with
  `KSPROPERTY_TYPE_BASICSUPPORT`. The gain table runs up to ×9.4 at 255, about 3.2 extra stops for dark rooms.
- Response: gamma 1.26 with black 0 (only 4 well-exposed points in the dim room). The fitted model
  agreed within ±0.07 stops across exposures. Noise: 0.21 levels.
- Repeated readings, opening and closing the camera for each: EV 3.07–3.15 over a minute.

**First-frame latency depends on the exposure mode when the stream starts:**

| Exposure mode at stream start | First frame after open |
|---|---|
| Auto | about 0.9 s |
| Manual | **about 5.2 s** |

The first version of `CameraSession` saved the camera's settings before the stream started. At that point
Windows reported exposure as not auto, so closing the session left the camera in manual mode, and every
later open took 4.5 s longer. That included other apps. Saving the settings after the stream starts fixes it:
the camera is left in auto, and metering takes about 0.5 s.

## Unstable readings with constant room light (2026-09-22)

The dashboard showed the light level jumping between two values about one stop apart while the room's
lighting was unchanged. Frame-by-frame recordings (`abctl settle`) showed two measurement faults:

- **The lock lands late.** The first ~5 frames after opening are still auto-exposed, and the locked
  exposure and gain take effect at frame 6 or 7. A fixed skip of 5 frames sometimes captured a transitional frame.
- **Concurrent sessions overwrite each other.** With a second AutoBrightness process using the camera,
  exposure read back as −6 after being set to −5, and the image dropped by exactly one stop mid-capture.

Fixes: wait until three consecutive frames agree; after capturing, check that exposure and gain still read back
as set and that the frames agree, otherwise retry; and a cross-process lock so AutoBrightness processes take turns.
Afterwards, twelve readings over a minute were 3.06–3.07, with an old copy of the app running alongside.

There is also a real effect: at night **the monitor is the main light source the camera sees**. It lights
the user's face and the wall, so readings follow the screen's content and whether the user is in frame. Aiming the
measurement area at a surface the screen does not light reduces this; compensating for it is future work.
