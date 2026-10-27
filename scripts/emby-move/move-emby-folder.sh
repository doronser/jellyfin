#!/usr/bin/env bash
# Usage: move-emby-folder.sh <repo-root> <Folder>...
# git mv Emby.Server.Implementations/<X> -> Jellyfin.Server.Implementations/<X> and rewrite namespaces solution-wide.
set -euo pipefail
root="$1"; shift
cd "$root"
for x in "$@"; do
  if [ -e "Jellyfin.Server.Implementations/$x" ]; then echo "COLLISION: $x" >&2; exit 1; fi
  git mv "Emby.Server.Implementations/$x" "Jellyfin.Server.Implementations/$x"
  # rewrite in all text sources (cs, csproj, sh, json, txt etc.), excluding build output
  git grep -lI "Emby\.Server\.Implementations\.$x\b" | while read -r f; do
    sed -i -E "s/Emby\.Server\.Implementations\.$x\b/Jellyfin.Server.Implementations.$x/g" "$f"
  done
done
