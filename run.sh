#!/usr/bin/env sh
# Run Daxis from source:            ./run.sh
# Build a single-file executable:   ./run.sh --publish   (output in dist/)
set -e
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null 2>&1; then
  echo "Daxis needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" >&2
  exit 1
fi
if [ "$1" = "--publish" ]; then
  case "$(uname -s)" in
    Linux*) rid=linux-x64 ;;
    *) rid=win-x64 ;;
  esac
  dotnet publish src/Daxis.App -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -o dist
  echo "Built: dist/"
else
  dotnet run --project src/Daxis.App -c Release
fi
