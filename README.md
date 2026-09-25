# Home Assistant Agent for SteamVR (Linux)
[![License](https://img.shields.io/badge/License-MIT-blue?style=for-the-badge)](#license)
![OS - Linux](https://img.shields.io/badge/OS-Linux-fcc624?style=for-the-badge&logo=linux&logoColor=black)

A Linux port of [Home Assistant Agent for SteamVR](https://github.com/Antoni-Czaplicki/SteamVR.HA-Agent).
It connects SteamVR to your Home Assistant instance. For the Home Assistant side, see the
[integration repo](https://github.com/Antoni-Czaplicki/SteamVR.HA).

The Windows app is a WinUI 3 desktop app. This fork replaces it with a small headless daemon that
uses the same WebSocket protocol, so the existing Home Assistant integration works without changes.

## Features

- Reports SteamVR status every second: whether the runtime process is running, OpenVR connection,
  headset activity level, the running app, and controller battery and charging state.
- Standard SteamVR notifications with an optional image (`imageData`, `imagePath` or `imageUrl`).
- Advanced notifications (`customProperties.enabled`): image overlays with anchors (world, head, left
  or right hand), offsets and rotation, opacity, in and out transitions, sine animations, follow mode,
  text areas and channels. On Windows these needed the separate `SteamVR.NotifyPlugin.exe`. Here they
  are rendered in-process with OpenVR overlays.
- Controller vibration (`vibrate_controller_left/right/both`) and `start_steamvr`.
- Forwarding of any OpenVR event (`register_event` / `unregister_event`).
- Registers itself with SteamVR, so SteamVR starts it automatically.
- Runs as a systemd user service, logging to the journal.

## Requirements

- SteamVR on Linux. Other OpenVR runtimes such as xrizer on Monado or WiVRn may work partially,
  depending on which OpenVR interfaces they implement.
- The .NET 10 SDK to build (`pacman -S dotnet-sdk`, `apt install dotnet-sdk-10.0`, ...). At runtime,
  either the .NET 10 runtime or a self-contained build.
- `libopenvr_api.so`. The agent uses your distro's `openvr` package if it is installed, and otherwise
  falls back to the copy that ships with Steam.

## Installation

```sh
git clone https://github.com/WentTheFox/SteamVR.HA-Agent-Linux.git
cd SteamVR.HA-Agent-Linux
./install.sh
```

This installs the app to `~/.local/share/steamvr-ha-agent/app` and links `~/.local/bin/steamvr-ha-agent`.
It also installs the systemd user unit `steamvr-ha-agent.service`.

Start SteamVR, then run the agent once with `systemctl --user start steamvr-ha-agent`. On first
connection the agent registers a SteamVR app manifest with auto-launch enabled. After that, SteamVR
starts the agent whenever SteamVR starts, and the agent exits when SteamVR quits.

To keep the agent running all the time instead, for example so Home Assistant can use `start_steamvr`:

```sh
./install.sh --enable      # enables the service at login
```

Then set `"exitWithSteamVR": false` in the config.

Other commands:

```sh
./install.sh uninstall                  # remove app, service and manifest; keeps config
SELF_CONTAINED=1 ./install.sh           # bundle the .NET runtime
journalctl --user -u steamvr-ha-agent -f
```

You can also run it straight from the source tree:
`dotnet run --project src/SteamVRHAAgent -- --verbose`.

## Configuration

`~/.config/steamvr-ha-agent/config.json` is created on first start:

| Key                           | Default | Description                                                                              |
|-------------------------------|---------|------------------------------------------------------------------------------------------|
| `port`                        | `8077`  | WebSocket port Home Assistant connects to                                                |
| `bindAddress`                 | `"+"`   | Listen address; `"+"` means all interfaces, `"127.0.0.1"` means local only               |
| `exitWithSteamVR`             | `true`  | Exit when SteamVR quits                                                                  |
| `autoLaunchWithSteamVR`       | `true`  | Register the SteamVR manifest so SteamVR launches the agent; `false` unregisters it      |
| `enableAdvancedNotifications` | `true`  | Allow image overlay notifications                                                        |
| `openVRLibraryPath`           | `null`  | Explicit path to `libopenvr_api.so`. You can also set the `OPENVR_API_PATH` env variable |
| `verboseLogging`              | `false` | Debug logging                                                                            |

Command line options: `--config <path>`, `--port <port>`, `--verbose`, `--print-config`, `--version`, `--help`.

If a firewall is running, allow TCP port 8077 from your Home Assistant host. For example:
`sudo firewall-cmd --add-port=8077/tcp --permanent` or `sudo ufw allow 8077/tcp`.

## Differences from the Windows app

- No GUI or tray icon. Settings live in the config file and status goes to the journal.
- Advanced notifications are built in rather than provided by a separate plugin. The notify-plugin
  status is gone, and the `notify_plugin_disabled` error now means advanced notifications are disabled
  in the config.
- `event_data` in forwarded OpenVR events contains the tracked device index. The Windows app always
  sent the placeholder string `Valve.VR.VREvent_Data_t`.
- Error responses now fill in `error.message`.
- `is_steamvr_process_running` is also true when Monado (`monado-service`) or WiVRn
  (`wivrn-server`) is running.
- No crash reporting or telemetry. The Windows app sent crash reports to Sentry.

## Project layout

```
src/SteamVRHAAgent/
  Program.cs                   CLI, single-instance lock, signal handling
  Agent.cs                     WebSocket message handling, state broadcasting, manifest registration
  WebSocketServer.cs           HttpListener-based WebSocket server (no ASP.NET runtime needed)
  Protocol/                    JSON messages shared with the Home Assistant integration
  VR/VRRuntime.cs              OpenVR connection; every OpenVR call runs on one dedicated thread
  VR/AdvancedNotifications.cs  Overlay-based image notifications
  VR/Images.cs                 Image loading and text drawing (ImageSharp)
  VR/SteamVRManifest.cs        SteamVR app manifest and launcher script
  OpenVR/openvr_api.cs         Valve's C# OpenVR bindings
packaging/steamvr-ha-agent.service
install.sh
```

## License

MIT, see [LICENSE](LICENSE). Based on
[SteamVR.HA-Agent](https://github.com/Antoni-Czaplicki/SteamVR.HA-Agent) by Antoni Czaplicki.
Inspired by [OpenVROverlayPipe](https://github.com/BOLL7708/OpenVROverlayPipe).
