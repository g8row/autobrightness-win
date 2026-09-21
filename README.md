# autobrightness-win

Camera-based automatic monitor brightness for Windows, driving [Twinkle Tray](https://twinkletray.com/).

A webcam with **locked exposure** is used as an ambient light meter; readings are smoothed and mapped
through a user-editable curve to a brightness level, which is sent to Twinkle Tray over its local
named pipe (`\.\pipe\twinkle-tray\cmds`).

Status: early spike — verifying camera metering. See [`docs/`](docs/).

## Layout

- `spikes/CameraProbe` — command-line tool to inspect camera controls and measure light with locked exposure.

## Requirements

- Windows 11, .NET 10 SDK
- A UVC webcam that exposes manual exposure (virtual cameras such as Camo/OBS do not)
- Twinkle Tray (Store or installer build)
