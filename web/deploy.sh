#!/usr/bin/env bash
# ---------------------------------------------------------------------------
#  Publish the PocketModem site to pocketmodem.emirbaycan.com.tr
#
#  The site is a subdomain of emirbaycan.com.tr and is served by that project's
#  existing nginx container, which routes on Host to /srv/sites/<name>. So an
#  update is a file copy - no container is rebuilt or restarted, and nothing
#  else on that host is touched.
#
#  The one-time setup (host mapping, edge server block, certificate) is already
#  done; this only refreshes the page.
#
#  Note for anyone changing that container's nginx.conf: it is bind-mounted as a
#  single file, so `sed -i` will not work. sed writes a new file and renames it
#  over the old one, leaving the container holding the original inode - the host
#  file changes, the container never sees it, and nginx -t passes on both sides
#  because each is reading a valid file. Recreating the container is what
#  repoints the mount.
# ---------------------------------------------------------------------------
set -euo pipefail

HOST=kx
REMOTE=/opt/projects/emirbaycan/sites/pocketmodem
LOCAL="$(cd "$(dirname "$0")" && pwd)/index.html"

echo "==> uploading"
scp "$LOCAL" "$HOST:$REMOTE/index.html"

echo "==> verifying"
code=$(curl -sL -o /dev/null -w "%{http_code}" --max-time 25 https://pocketmodem.emirbaycan.com.tr/)
if [ "$code" = "200" ]; then
  echo "    ok - https://pocketmodem.emirbaycan.com.tr/"
else
  echo "    unexpected status $code" >&2
  exit 1
fi
