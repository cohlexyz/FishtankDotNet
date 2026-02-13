#!/usr/bin/env bash
set -e

APP_DIR="$(cd "KfChatDotNetBot/bin/Debug/net10.0" && pwd)"
APP_DLL="./KfChatDotNetBot"

podman run --rm \
  --network=host \
  -v "$APP_DIR:/app" \
  -w /app \
  mcr.microsoft.com/dotnet/runtime:10.0 \
  $APP_DLL
