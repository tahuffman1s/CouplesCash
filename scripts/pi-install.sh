#!/usr/bin/env bash
# Kept for old links. The real installer is ../install.sh (any 64-bit Linux, incl. Raspberry Pi):
#   curl -fsSL https://raw.githubusercontent.com/tahuffman1s/CouplesCash/main/install.sh | bash
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$here/../install.sh" ]]; then
  exec bash "$here/../install.sh" "$@"
fi
BRANCH="${COUPLESCASH_BRANCH:-${BRANCH:-main}}"
curl -fsSL "https://raw.githubusercontent.com/tahuffman1s/CouplesCash/${BRANCH}/install.sh" | bash -s -- "$@"
