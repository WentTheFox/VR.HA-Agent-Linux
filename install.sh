#!/usr/bin/env bash
# Installs the agent for the current user (no root needed). From a release archive it installs the
# prebuilt app next to this script; from a source checkout it builds with the .NET SDK first.
#
#   ./install.sh               install (SteamVR can still launch the agent on demand)
#   ./install.sh --enable      also enable the systemd user service at login (recommended, needed for Monado)
#   ./install.sh uninstall     remove the app, service and SteamVR manifest (config is kept)
#
# When building from source, set SELF_CONTAINED=1 to bundle the .NET runtime instead of using the system one.
set -euo pipefail

APP_ID=steamvr-ha-agent
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/$APP_ID"
APP_DIR="$DATA_DIR/app"
BIN_DIR="$HOME/.local/bin"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
UNIT="$APP_ID.service"
DESKTOP_FILE="${XDG_DATA_HOME:-$HOME/.local/share}/applications/$APP_ID.desktop"

have_systemd() { command -v systemctl >/dev/null 2>&1 && systemctl --user show-environment >/dev/null 2>&1; }

install_app() {
    local enable=0
    [[ "${1:-}" == "--enable" ]] && enable=1

    local prebuilt="$REPO_DIR/app"
    if [[ ! -x "$prebuilt/$APP_ID" ]]; then
        prebuilt=""
        command -v dotnet >/dev/null || { echo "dotnet SDK not found (Arch: pacman -S dotnet-sdk)" >&2; exit 1; }
    fi

    local was_active=0
    if have_systemd && systemctl --user is-active --quiet "$UNIT"; then
        was_active=1
        systemctl --user stop "$UNIT"
    fi

    rm -rf -- "$APP_DIR.new"
    mkdir -p "$DATA_DIR"
    if [[ -n "$prebuilt" ]]; then
        echo "Installing prebuilt app..."
        cp -a -- "$prebuilt" "$APP_DIR.new"
    else
        local publish_args=(-c Release -o "$APP_DIR.new")
        if [[ "${SELF_CONTAINED:-0}" == "1" ]]; then
            case "$(uname -m)" in
                x86_64) publish_args+=(-r linux-x64) ;;
                aarch64) publish_args+=(-r linux-arm64) ;;
                *) echo "Unsupported architecture for SELF_CONTAINED: $(uname -m)" >&2; exit 1 ;;
            esac
            publish_args+=(--self-contained true)
        else
            publish_args+=(--self-contained false)
        fi

        echo "Building..."
        dotnet publish "$REPO_DIR/src/SteamVRHAAgent/SteamVRHAAgent.csproj" "${publish_args[@]}"
    fi
    rm -rf -- "$APP_DIR"
    mv -- "$APP_DIR.new" "$APP_DIR"

    mkdir -p "$BIN_DIR"
    ln -sf "$APP_DIR/$APP_ID" "$BIN_DIR/$APP_ID"
    echo "Installed to $APP_DIR (command: $BIN_DIR/$APP_ID)"

    # App menu entry. The launcher runs the agent through its service when installed; launching it
    # again just brings the running agent's window to the front. (A script keeps the desktop entry's
    # Exec line within what the spec allows.)
    cat > "$APP_DIR/open.sh" <<LAUNCHER
#!/bin/sh
systemctl --user start $UNIT 2>/dev/null
exec "$APP_DIR/$APP_ID" "\$@"
LAUNCHER
    chmod +x "$APP_DIR/open.sh"

    mkdir -p "$(dirname "$DESKTOP_FILE")"
    cat > "$DESKTOP_FILE" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Home Assistant Agent for SteamVR
Comment=Connect SteamVR or Monado to Home Assistant
Exec="$APP_DIR/open.sh"
Icon=$APP_DIR/Assets/icon.png
Terminal=false
Categories=Utility;
StartupWMClass=$APP_ID
DESKTOP
    if command -v update-desktop-database >/dev/null; then
        update-desktop-database -q "$(dirname "$DESKTOP_FILE")" || true
    fi
    echo "Installed app menu entry"

    if have_systemd; then
        mkdir -p "$UNIT_DIR"
        sed "s|@EXEC@|$APP_DIR/$APP_ID|" "$REPO_DIR/packaging/$UNIT" > "$UNIT_DIR/$UNIT"
        systemctl --user daemon-reload
        echo "Installed systemd user service $UNIT"
        if (( enable )); then
            # reenable refreshes the install symlinks in case WantedBy changed.
            systemctl --user reenable "$UNIT"
            systemctl --user start "$UNIT"
            echo "Enabled $UNIT at login"
        elif (( was_active )); then
            systemctl --user start "$UNIT"
        fi
    fi

    cat <<EOF

Next steps:
  1. Open "Home Assistant Agent for SteamVR" from your app menu. Its settings match the Windows app:
     "Start with login" runs it at every login (same as ./install.sh --enable), and "Auto Start"
     lets SteamVR launch it. It connects to SteamVR or Monado whenever one of them is running.
  2. Add the SteamVR integration in Home Assistant, pointing it at this machine on port 8077.

Config: ${XDG_CONFIG_HOME:-$HOME/.config}/$APP_ID/config.json   Logs: journalctl --user -u $UNIT -f
EOF
}

uninstall_app() {
    if have_systemd; then
        systemctl --user disable --now "$UNIT" 2>/dev/null || true
        rm -f -- "$UNIT_DIR/$UNIT"
        systemctl --user daemon-reload
    fi
    rm -f -- "$BIN_DIR/$APP_ID" "$DESKTOP_FILE"
    rm -rf -- "$APP_DIR"
    # SteamVR ignores registered manifests whose file no longer exists.
    rm -f -- "$DATA_DIR/$APP_ID.vrmanifest" "$DATA_DIR/launch-from-steamvr.sh" "$DATA_DIR/icon.png"
    rmdir -- "$DATA_DIR" 2>/dev/null || true
    echo "Uninstalled. Config left in ${XDG_CONFIG_HOME:-$HOME/.config}/$APP_ID"
}

case "${1:-install}" in
    install) shift || true; install_app "$@" ;;
    --enable) install_app --enable ;;
    uninstall) uninstall_app ;;
    *) echo "Usage: $0 [install [--enable] | --enable | uninstall]" >&2; exit 2 ;;
esac
