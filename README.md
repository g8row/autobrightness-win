# AutoBrightness

Automatic monitor brightness for Windows desktops, using a webcam as the light sensor and
[Twinkle Tray](https://twinkletray.com/) to set brightness.

Most webcam-brightness tools average the picture while the camera's auto-exposure is on. Auto-exposure
keeps the picture at roughly the same brightness, so those readings barely change. AutoBrightness locks
the camera's exposure, gain and white balance. It measures each camera model's tone curve with a
calibration run and converts the picture into a light level that stays the same at any exposure setting.

![Dashboard](docs/screenshots/dashboard.png)

| Camera and calibration | Brightness curve |
|---|---|
| ![Camera page](docs/screenshots/camera.png) | ![Curve editor](docs/screenshots/curve.png) |

## Install

Download the installer for your PC from [Releases](https://github.com/g8row/autobrightness-win/releases):
`win-x64` for most PCs, `win-arm64` for Windows on ARM.

- It installs for your user only, with no administrator prompt, into `%LOCALAPPDATA%\Programs\AutoBrightness`.
- It adds a Start menu shortcut and starts the app.
- Newer versions upgrade in place and close the running app first.
- Uninstall from **Settings > Apps**. That also removes *Start with Windows*; your settings and camera
  calibration in `%LOCALAPPDATA%\AutoBrightness` are kept.

Each installer is built from its release tag by [GitHub Actions](.github/workflows/release.yml) and carries a signed
[build provenance attestation](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations).
To check that an installer really came from this repository's workflow:

```
gh attestation verify AutoBrightness-0.2.0-win-x64.msi --repo g8row/autobrightness-win
```

SHA-256 checksums are in each release's `SHA256SUMS.txt`. The installer isn't code-signed, so SmartScreen may warn before it runs.

## How it works

1. **Measure:** about every 20 s, the camera opens for about half a second at 160×120. Exposure is
   locked and moved in steps (gain is used when it's too dark) until the chosen area is well exposed. The
   camera's own settings are restored afterwards.
2. **Smooth:** a median filter ignores one-off changes, such as someone walking past. The reading reacts
   faster when the room gets brighter than when it gets darker.
3. **Map:** an editable curve turns light level into brightness. When you change brightness in Twinkle
   Tray yourself, that becomes a learned point on the curve.
4. **Apply (Automatic mode):** the new brightness goes to Twinkle Tray over its local pipe
   (`\\.\pipe\twinkle-tray\cmds`) as a single step. Changes are deliberately rare: at least 10%, at most every
   15 minutes and 48 per day, because many monitors wear out their settings memory after about 100k writes.
   Changes of 30% or more, such as switching on the room lights, apply straight away. An optional fade is in Settings.

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

Set `AUTOBRIGHTNESS_DATA` to use a different data folder (portable use, or a second copy for testing).

To build the installer locally, publish the app and then build `installer/`:

```
dotnet publish src/AutoBrightness.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64
dotnet build installer -c Release -p:InstallerPlatform=x64
```

To release, push a tag such as `v0.2.0`. The release workflow tests, builds the x64 and ARM64 installers, attests them and publishes the release.

## Layout

| Path | What it is |
|---|---|
| `src/AutoBrightness.Core` | Camera metering, calibration, crash recovery, Twinkle Tray client, control loop, settings (no UI) |
| `src/AutoBrightness.App` | WinUI 3 tray app: dashboard, camera and calibration page, curve editor, settings |
| `installer` | WiX MSI: per-user install, Start menu shortcut, upgrades and uninstall cleanup |
| `tests/AutoBrightness.Core.Tests` | Unit tests, including a simulated camera |
| `tools/abctl` | Camera-only command-line diagnostics: `cameras`, `calibrate`, `measure`, `frames`, `startup`, `simulate-crash`, `recover` |
| `spikes/CameraProbe` | The first exploration spike (uses DirectShow; kept for reference) |
| `docs/` | [Research](docs/research.md) and [hardware findings](docs/camera-probe-findings.md) |

Settings, camera profiles and state are stored in `%LOCALAPPDATA%\AutoBrightness`.
