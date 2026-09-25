# Privacy Policy

Home Assistant Agent for SteamVR (Linux) processes SteamVR device, application, and connection
information on your computer. It makes this information available over a local WebSocket server to
the Home Assistant clients that you choose to connect.

The processed information may include:

- SteamVR connection and process status;
- the name of the running VR application;
- headset activity and controller status; and
- settings that you configure in the config file.

The agent does not send this information to the developer. It includes no analytics, crash
reporting or advertising, and the developer does not sell or share data with third parties. Data
stays on your devices and local network unless you configure other software or networking to send
it elsewhere.

The agent downloads images only when a connected client asks it to show a notification with an
`imageUrl`.

Settings are stored in `~/.config/steamvr-ha-agent/config.json`. The SteamVR manifest and launcher
script are stored in `~/.local/share/steamvr-ha-agent/`. To stop processing, stop or uninstall the
agent.
