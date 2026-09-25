#!/usr/bin/env bash
# Builds a self-contained release archive: packaging/package.sh <rid> <version> [output-dir]
#   rid:     linux-x64 or linux-arm64
#   version: e.g. 0.3.0 or 0.3.0-ci.42
# Produces <output-dir>/vr-ha-agent-<version>-<rid>.tar.gz containing the app plus install.sh.
set -euo pipefail

RID="${1:?usage: package.sh <rid> <version> [output-dir]}"
VERSION="${2:?usage: package.sh <rid> <version> [output-dir]}"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="$(realpath -m "${3:-$REPO_DIR/dist}")"

NAME="vr-ha-agent-$VERSION-$RID"
STAGE="$OUT_DIR/$NAME"

rm -rf -- "$STAGE"
mkdir -p "$STAGE/packaging"

dotnet publish "$REPO_DIR/src/VRHAAgent/VRHAAgent.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:Version="$VERSION" \
    -o "$STAGE/app"

cp "$REPO_DIR/install.sh" "$REPO_DIR/README.md" "$REPO_DIR/LICENSE" "$REPO_DIR/PRIVACY.md" "$STAGE/"
cp "$REPO_DIR/packaging/vr-ha-agent.service" "$STAGE/packaging/"

tar -C "$OUT_DIR" -czf "$OUT_DIR/$NAME.tar.gz" "$NAME"
rm -rf -- "$STAGE"
echo "$OUT_DIR/$NAME.tar.gz"
