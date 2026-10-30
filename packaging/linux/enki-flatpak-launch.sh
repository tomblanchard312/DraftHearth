#!/bin/sh
# Flatpak command. Installs or updates the host Enki, then runs Qanloom.
set -eu
if [ -x /app/lib/enki-payload/enki-launch ]; then
  exec /app/lib/enki-payload/enki-launch /app/bin/nisaba-bin "$@"
fi
exec /app/bin/nisaba-bin "$@"
