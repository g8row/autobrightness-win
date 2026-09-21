# AutoBrightness

Automatic monitor brightness for Windows desktops, using a webcam as the light sensor and
[Twinkle Tray](https://twinkletray.com/) to set brightness.

Most webcam-brightness tools average the picture while the camera's auto-exposure is on. Auto-exposure
keeps the picture at roughly the same brightness, so those readings barely change. AutoBrightness locks
the camera's exposure, gain and white balance. It measures each camera model's tone curve with a
calibration run and converts the picture into a light level that stays the same at any exposure setting.

## How it works

1. **Measure:** about every 20 s, the camera opens for about half a second at 160×120. Exposure is
   locked and moved in steps (gain is used when it's too dark) until the chosen area is well exposed. The
   camera's own settings are restored afterwards.
2. **Smooth:** a median filter ignores one-off changes, such as someone walking past. The reading reacts
   faster when the room gets brighter than when it gets darker.
3. **Map:** an editable curve turns light level into brightness. When you change brightness in Twinkle
   Tray yourself, that becomes a learned point on the curve.
4. **Apply (Automatic mode):** the new brightness goes to Twinkle Tray over its local pipe
   (`\\.\pipe\twinkle-tray\cmds`). Changes are limited by a minimum step, a minimum interval and a daily
   budget, because many monitors wear out their settings memory after about 100k writes.

Modes: **Off** · **Preview** (measures, shows the target and learns, but never changes brightness) ·
**Automatic**. New installs start in Preview.

## Requirements

- Windows 10 2004 or later (developed on Windows 11)
- A USB webcam that allows manual exposure. Virtual cameras such as Camo or OBS can't be used.
- Twinkle Tray, running

## Building

```
dotnet build AutoBrightness.slnx
dotnet test --project tests/AutoBrightness.Core.Tests
```

This needs the .NET 10 SDK; Visual Studio is optional. The app is unpackaged and self-contained with
respect to the Windows App SDK.

Run `src/AutoBrightness.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/AutoBrightness.exe`.
Command-line switches:

- `--background`: start in the tray without showing the window (used by *Start with Windows*)
- `--exit`: ask the running instance to shut down cleanly

## Layout

| Path | What it is |
|---|---|
| `src/AutoBrightness.Core` | Camera metering, calibration, crash recovery, Twinkle Tray client, control loop, settings (no UI) |
| `src/AutoBrightness.App` | WinUI 3 tray app: dashboard, camera and calibration page, curve editor, settings |
| `tests/AutoBrightness.Core.Tests` | Unit tests, including a simulated camera |
| `tools/abctl` | Camera-only command-line diagnostics: `cameras`, `calibrate`, `measure`, `frames`, `startup`, `simulate-crash`, `recover` |
| `spikes/CameraProbe` | The first exploration spike (uses DirectShow; kept for reference) |
| `docs/` | [Research](docs/research.md) and [hardware findings](docs/camera-probe-findings.md) |

Settings, camera profiles and state are stored in `%LOCALAPPDATA%\AutoBrightness`.
