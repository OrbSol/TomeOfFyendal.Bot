#!/usr/bin/env bash
# Publishes a Release build of the bot and deploys it to the VPS over WireGuard.
#   ./deploy/deploy.sh
#   DEPLOY_HOST=user@10.100.0.1 ./deploy/deploy.sh
# The server keeps its own appsettings.Production.json (never shipped from here).
set -euo pipefail

DEPLOY_HOST="${DEPLOY_HOST:-claude-code@10.100.0.1}"
DEPLOY_KEY="${DEPLOY_KEY:-$HOME/.ssh/claude_deploy}"
REMOTE_DIR="${REMOTE_DIR:-/var/www/tome-bot/publish}"
SERVICE="${SERVICE:-tome-bot}"

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/deploy-out"
rm -rf "$out" && mkdir -p "$out"

dotnet publish "$root/src/TomeOfFyendal.Bot/TomeOfFyendal.Bot.csproj" -c Release -o "$out/app" --nologo -v q
# Never ship environment-specific config from a workstation.
find "$out/app" -name 'appsettings.*.json' ! -name 'appsettings.Example.json' -delete

tar -czf "$out/tome-bot.tar.gz" -C "$out/app" .
scp -i "$DEPLOY_KEY" -q "$out/tome-bot.tar.gz" "$DEPLOY_HOST:/tmp/tome-bot.tar.gz"
ssh -i "$DEPLOY_KEY" "$DEPLOY_HOST" "sudo tar -xf /tmp/tome-bot.tar.gz -C '$REMOTE_DIR' --owner=33 --group=33 && rm /tmp/tome-bot.tar.gz && sudo systemctl restart '$SERVICE' && sleep 5 && systemctl is-active '$SERVICE'"
rm -rf "$out"
echo "Deployed."
