#!/usr/bin/env bash
set -euo pipefail

dotnet --list-sdks | grep -q '^8\.'
node --version
npm --version
test -d "${PLAYWRIGHT_BROWSERS_PATH}"
find "${PLAYWRIGHT_BROWSERS_PATH}" -maxdepth 4 -type f \( -name chrome -o -name headless_shell \) -print -quit | grep -q .

echo "MnaiWork E2B toolchain verified."