# Home Assistant Agent for VR (Linux)
[![License](https://img.shields.io/badge/License-MIT-blue?style=for-the-badge)](#license)
![OS - Linux](https://img.shields.io/badge/OS-Linux-fcc624?style=for-the-badge&logo=linux&logoColor=black)

A Linux port of [Home Assistant Agent for SteamVR](https://github.com/Antoni-Czaplicki/SteamVR.HA-Agent),
renamed because it works with Monado as well as SteamVR.
It connects SteamVR or [Monado](https://monado.freedesktop.org/) to your Home Assistant instance. For the Home Assistant side, see the
[integration repo](https://github.com/Antoni-Czaplicki/SteamVR.HA).

It keeps the Windows app's window and tray icon, rebuilt with [Avalonia](https://avaloniaui.net/) and
[FluentAvalonia](https://github.com/amwx/FluentAvalonia) so it looks and behaves like the WinUI
original. It speaks the same WebSocket protocol, so the existing Home Assistant integration works
without changes.

## Supported runtimes

The agent watches for running VR runtimes and connects to whichever one it finds:

| Feature                                 | SteamVR               | Monado (incl. Envision builds)                         |
|-----------------------------------------|-----------------------|--------------------------------------------------------|
| Runtime and connection status           | ✅                    | ✅                                                     |
| Running app                             | ✅                    | ✅ focused OpenXR client                               |
| Controller battery and charging state   | ✅                    | ✅                                                     |
| Headset activity level                  | ✅                    | ≈ `1` while an app is focused, otherwise `0`           |
| Basic notifications (with image)        | ✅ SteamVR            | ✅ through [WayVR](https://github.com/wlx-team/wayvr)  |
| Advanced (overlay) notifications        | ✅                    | ❌                                                     |
| Controller vibration                    | ✅                    | ✅ through `wayvrctl haptics`                          |
| OpenVR events                           | ✅                    | ❌                                                     |
| `start_steamvr` command                 | launches SteamVR      | runs `startRuntimeCommand` (see config)                |
| Auto-launch                             | "Auto Start" (SteamVR manifest) | "Start with login"                           |

- **SteamVR** uses OpenVR. The agent only connects while `vrserver` is running, so it never launches
  SteamVR itself. It also never loads xrizer when `openvrpaths.vrpath` points at xrizer.
- **Monado** uses libmonado, Monado's management API, and only connects while `monado-service` is
  running, so it never socket-activates Monado. libmonado must come from the same Monado build as
  the running service. The agent looks for it next to the running `monado-service` binary (install
  prefix or build tree, which covers Envision), then in system locations. Set `libMonadoPath` if it
  isn't found. WiVRn (`wivrn-server`) is detected too, but untested.
- Notifications and vibration on Monado need WayVR running. Notifications are sent as XSOverlay-style
  messages to WayVR's listener on UDP `127.0.0.1:42069`.

## Features

- Reports runtime status every second: whether the runtime process is running, OpenVR connection,
  headset activity level, the running app, and controller battery and charging state.
- Standard SteamVR notifications with an optional image (`imageData`, `imagePath` or `imageUrl`).
- Advanced notifications (`customProperties.enabled`): image overlays with anchors (world, head, left
  or right hand), offsets and rotation, opacity, in and out transitions, sine animations, follow mode,
  text areas and channels. On Windows these needed the separate `SteamVR.NotifyPlugin.exe`. Here they
  are rendered in-process with OpenVR overlays.
- Controller vibration (`vibrate_controller_left/right/both`) and `start_steamvr`.
- Forwarding of any OpenVR event (`register_event` / `unregister_event`).
- Registers itself with SteamVR, so SteamVR starts it automatically.
- The same window as the Windows app: a **Home** page with runtime, WebSocket server and notify plugin
  status; **Notification editor**; and **Settings** with port, launch minimized, always on top, tray
  icon, start with login, exit with SteamVR, advanced notifications, and auto start. A tray icon offers
  Show Window / Exit.
- Runs as a systemd user service tied to your desktop session, logging to the journal. Without a
  display (or with `--headless`) it runs without the window.

## Requirements

- SteamVR or Monado on Linux, and optionally WayVR for notifications and vibration on Monado.
- A desktop with a system tray (KDE Plasma works out of the box; GNOME needs the AppIndicator extension).
- The .NET 10 SDK to build (`pacman -S dotnet-sdk`, `apt install dotnet-sdk-10.0`, ...). At runtime,
  either the .NET 10 runtime or a self-contained build.
- `libopenvr_api.so`. The agent uses your distro's `openvr` package if it is installed, and otherwise
  falls back to the copy that ships with Steam.

## Installation

### From a release (recommended)

Download `vr-ha-agent-<version>-linux-x64.tar.gz` (or `-linux-arm64`) from the
[latest release](https://github.com/WentTheFox/VR.HA-Agent-Linux/releases/latest). The builds are
self-contained, so .NET isn't needed.

```sh
tar -xzf vr-ha-agent-*-linux-x64.tar.gz
cd vr-ha-agent-*-linux-x64
./install.sh --enable
```

To update, do the same with the newer archive. Your settings are kept. This also works for installs
from before the rename (0.3.x, `steamvr-ha-agent`): the installer removes the old service, command
and menu entry and moves your settings to `~/.config/vr-ha-agent/`.

### From source

Needs the .NET 10 SDK.

```sh
git clone https://github.com/WentTheFox/VR.HA-Agent-Linux.git
cd VR.HA-Agent-Linux
./install.sh --enable
```

### After installing

Open **Home Assistant Agent for VR** from your app menu. `--enable` turns on "Start with login",
which you can toggle in Settings later. For Monado, keep "Start with login" on:
unlike SteamVR, Monado can't launch the agent.

This installs the app to `~/.local/share/vr-ha-agent/app`, links `~/.local/bin/vr-ha-agent`,
adds an app menu entry, and installs the systemd user unit `vr-ha-agent.service`. Launching the
app while it's already running brings its window to the front.

With "Auto Start" on (the default), the agent registers a SteamVR app manifest the first time it
connects to SteamVR. From then on, SteamVR launches the agent.

Other commands:

```sh
./install.sh uninstall                  # remove app, service and manifest; keeps config
SELF_CONTAINED=1 ./install.sh           # from source: bundle the .NET runtime
packaging/package.sh linux-x64 0.3.0    # build a release archive into dist/
journalctl --user -u vr-ha-agent -f
```

You can also run it straight from the source tree:
`dotnet run --project src/VRHAAgent -- --verbose`.

## Configuration

Settings are changed in the app's Settings page and stored in `~/.config/vr-ha-agent/config.json`.
If you edit that file by hand, do it while the app is closed:

| Key                           | Default | Description                                                                              |
|-------------------------------|---------|------------------------------------------------------------------------------------------|
| `port`                        | `8077`  | WebSocket port Home Assistant connects to                                                |
| `bindAddress`                 | `"+"`   | Listen address; `"+"` means all interfaces, `"127.0.0.1"` means local only               |
| `launchMinimized`             | `false` | Start hidden in the tray (or minimized when the tray icon is off)                        |
| `enableTray`                  | `true`  | Show the tray icon; minimizing hides the window to the tray                              |
| `alwaysOnTop`                 | `false` | Keep the window above other windows                                                      |
| `runtime`                     | `"auto"`| `"auto"`, `"steamvr"` or `"monado"`: which runtimes to connect to                        |
| `exitWithSteamVR`             | `false` | Exit when the runtime (SteamVR or Monado) quits                                          |
| `startRuntimeCommand`         | `null`  | Shell command for `start_steamvr`. Default: SteamVR via Steam, or `systemctl --user start monado.service` when `runtime` is `"monado"` (e.g. set it to `envision -S`) |
| `autoLaunchWithSteamVR`       | `true`  | Register the SteamVR manifest so SteamVR launches the agent; `false` unregisters it      |
| `enableAdvancedNotifications` | `true`  | Allow image overlay notifications                                                        |
| `openVRLibraryPath`           | `null`  | Explicit path to `libopenvr_api.so`. You can also set the `OPENVR_API_PATH` env variable |
| `libMonadoPath`               | `null`  | Explicit path to `libmonado.so`. You can also set the `LIBMONADO_PATH` env variable      |
| `verboseLogging`              | `false` | Debug logging                                                                            |

Command line options: `--config <path>`, `--port <port>`, `--verbose`, `--headless`, `--print-config`,
`--version`, `--help`.

If a firewall is running, allow TCP port 8077 from your Home Assistant host. Some distros enable one by
default; CachyOS, for example, ships with ufw on. Home Assistant then shows the agent as disconnected
even though it runs fine; `journalctl -k | grep 'UFW BLOCK.*DPT=8077'` shows the blocked attempts.

```sh
sudo ufw allow from 192.168.1.0/24 to any port 8077 proto tcp   # ufw, your LAN or the HA host's IP
sudo firewall-cmd --permanent --add-port=8077/tcp && sudo firewall-cmd --reload   # firewalld
```

## Differences from the Windows app

- "Start with Windows" is "Start with login" (the systemd user service), and "Auto Start" doesn't ask
  where to save the manifest; it lives in `~/.local/share/vr-ha-agent/`.
- The Notification editor page opens the editor in your browser instead of an embedded WebView.
- Settings has two extra Linux-only entries: VR Runtime and Start runtime command.
- Advanced notifications are built in rather than provided by a separate plugin. The "Notify Plugin
  Status" card shows whether they can be shown right now, and the `notify_plugin_disabled` error means
  they're turned off in Settings.
- `event_data` in forwarded OpenVR events contains the tracked device index. The Windows app always
  sent the placeholder string `Valve.VR.VREvent_Data_t`.
- Error responses now fill in `error.message`.
- Monado support (see above). With Monado, `is_openvr_connected` means "connected to Monado", and
  `is_steamvr_process_running` means "some VR runtime is running".
- "Exit with SteamVR" is off by default, so the agent keeps running between VR sessions.
- No crash reporting or telemetry. The Windows app sent crash reports to Sentry.

## Project layout

```
src/VRHAAgent/
  Program.cs                   CLI, startup, signal handling
  SingleInstance.cs            One instance per user; a second launch activates the running one
  UI/                          Avalonia window (Home, Notification editor, Settings) and tray icon
  Agent.cs                     WebSocket message handling, state broadcasting, manifest registration
  WebSocketServer.cs           HttpListener-based WebSocket server (no ASP.NET runtime needed)
  Protocol/                    JSON messages shared with the Home Assistant integration
  VR/VRRuntime.cs              OpenVR connection; every OpenVR call runs on one dedicated thread
  VR/AdvancedNotifications.cs  Overlay-based image notifications
  VR/Images.cs                 Image loading and text drawing (ImageSharp)
  VR/SteamVRManifest.cs        SteamVR app manifest and launcher script
  Monado/MonadoBackend.cs      Monado state via libmonado
  Monado/LibMonado.cs          libmonado bindings, loaded from the running service's build
  Monado/WayVR.cs              Notifications and haptics through WayVR
  RuntimeProcesses.cs          Detects running SteamVR / Monado / WiVRn / WayVR processes
  OpenVR/openvr_api.cs         Valve's C# OpenVR bindings
packaging/vr-ha-agent.service
packaging/package.sh           Builds a self-contained release archive
install.sh
.github/workflows/build.yml    CI: builds x64/arm64 on every push; publishes a release for v* tags
```

## Releasing

Bump `<Version>` in `src/VRHAAgent/VRHAAgent.csproj`, then push a matching tag:

```sh
git tag v0.3.0 && git push origin v0.3.0
```

CI builds both architectures and publishes a GitHub release with the archives and `SHA256SUMS`.
Tags with a suffix, such as `v0.4.0-beta.1`, are published as pre-releases. Every other push uploads
the archives as workflow artifacts.

## License

MIT, see [LICENSE](LICENSE). Based on
[SteamVR.HA-Agent](https://github.com/Antoni-Czaplicki/SteamVR.HA-Agent) by Antoni Czaplicki.
Inspired by [OpenVROverlayPipe](https://github.com/BOLL7708/OpenVROverlayPipe).
