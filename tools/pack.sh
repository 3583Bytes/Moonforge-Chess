#!/usr/bin/env bash
# Build the NuGet package and refresh the Unity UPM package's bundled DLL from
# source, so both artifacts are reproducible from a clean checkout.
#
# Usage: tools/pack.sh
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> Packing NuGet package (net10.0 + netstandard2.1)"
dotnet pack ChessCoreEngine -c Release -nologo

echo "==> Building netstandard2.1 assembly for Unity"
dotnet build ChessCoreEngine -c Release -f netstandard2.1 -nologo

echo "==> Refreshing Unity package DLL"
cp ChessCoreEngine/bin/Release/netstandard2.1/MoonforgeChess.Engine.dll \
   unity/com.adamberent.moonforge-chess/Runtime/MoonforgeChess.Engine.dll

echo ""
echo "Done."
echo "  NuGet:  $(ls -1 ChessCoreEngine/bin/Release/MoonforgeChess.Engine.*.nupkg | tail -1)"
echo "  Unity:  unity/com.adamberent.moonforge-chess/ (Runtime/MoonforgeChess.Engine.dll refreshed)"
