#!/usr/bin/env bash
# Dev helper: copy your Discord bot token to the clipboard, then run this.
# It reads the token from the Wayland clipboard (never printing it), saves it into the
# test server's Watch Party settings, and waits for the bot to connect.
# Usage: dev/set-bot-token.sh [server] [user] [password]
set -euo pipefail
B="${1:-http://127.0.0.1:8097}"; USER_NAME="${2:-admin}"; PASS="${3:-wptest}"
GUID=6f1c8a52-3d0e-4b7a-9c41-2e5d8f7a1b90

TOKEN="$(wl-paste --no-newline 2>/dev/null | tr -d '[:space:]' || true)"
if [[ ! "$TOKEN" =~ ^[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{20,}$ ]]; then
  echo "The clipboard doesn't look like a Discord bot token (three parts separated by dots)."
  echo "In the Developer Portal: Bot -> Reset Token -> Copy, then run this again."
  exit 1
fi

AUTH='MediaBrowser Client="wpdev", Device="cli", DeviceId="wpdev-cli", Version="1.0"'
ACCESS=$(curl -sf -XPOST "$B/Users/AuthenticateByName" -H 'Content-Type: application/json' -H "Authorization: $AUTH" \
  -d "{\"Username\":\"$USER_NAME\",\"Pw\":\"$PASS\"}" | python3 -c 'import sys,json;print(json.load(sys.stdin)["AccessToken"])')
A="Authorization: $AUTH, Token=\"$ACCESS\""

curl -sf "$B/Plugins/$GUID/Configuration" -H "$A" \
  | WP_TOKEN="$TOKEN" python3 -c '
import json, os, sys
c = json.load(sys.stdin)
c["DiscordBotToken"] = os.environ["WP_TOKEN"]
if c.get("VoteDurationSeconds", 60) < 30:
    c["VoteDurationSeconds"] = 30
print(json.dumps(c))' \
  | curl -sf -o /dev/null -XPOST "$B/Plugins/$GUID/Configuration" -H "$A" -H 'Content-Type: application/json' --data-binary @-
echo "Token saved (not shown). Waiting for the bot to connect..."

for _ in $(seq 1 20); do
  STATUS=$(curl -sf "$B/WatchParty/Status" -H "$A")
  STATE=$(echo "$STATUS" | python3 -c 'import sys,json;print(json.load(sys.stdin).get("State",""))')
  if [[ "$STATE" == "Connected" || "$STATE" == "InvalidToken" ]]; then break; fi
  sleep 1
done
echo "$STATUS" | python3 -c '
import sys, json
s = json.load(sys.stdin)
state = s.get("State")
if state == "Connected":
    print("Connected as", s.get("BotUserName"))
    print("Invite link:", s.get("InviteUrl"))
elif state == "InvalidToken":
    print("Discord rejected that token. Reset it in the Developer Portal and try again.")
else:
    print("Still", state, "- check: podman logs wp-test | grep -i discord")'
