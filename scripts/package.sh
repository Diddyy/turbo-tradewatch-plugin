#!/usr/bin/env sh
# Builds the plugin folder a Turbo server loads: dist/tradewatch/{TurboTradeWatch.dll,TurboTradeWatch.xml,manifest.json}.
# TURBO_CLOUD: the turbo-cloud checkout to build against (default ../turbo-cloud), already built.
set -e
cd "$(dirname "$0")/.."
TURBO_CLOUD="${TURBO_CLOUD:-$(pwd)/../turbo-cloud}"
CONFIGURATION="${TURBO_CLOUD_CONFIGURATION:-Debug}"
rm -rf dist/tradewatch
dotnet build TurboTradeWatch/TurboTradeWatch.csproj -c Release -nologo -v q \
  -p:TurboCloudPath="$TURBO_CLOUD" -p:TurboCloudConfiguration="$CONFIGURATION" \
  -p:DesignTime=false -o dist/tradewatch
# Only the plugin's own files: everything else is the host's. The XML documentation is the
# settings' summaries on the admin panel's Settings page.
find dist/tradewatch -type f ! -name 'TurboTradeWatch.dll' ! -name 'TurboTradeWatch.pdb' ! -name 'TurboTradeWatch.xml' ! -name 'manifest.json' -delete
find dist/tradewatch -mindepth 1 -type d -empty -delete
ls dist/tradewatch
