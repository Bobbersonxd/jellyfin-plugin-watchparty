#!/usr/bin/env python3
"""Dev tool: pretend to be a remote-controllable Jellyfin player ("WatchPartyTestTV").

Logs every Playstate / Play command the server sends, one JSON line per message, so the
Watch Party plugin can be tested end to end without a real TV or browser.

Usage: dev/fake-player.py [server] [user] [password]
Needs the `websocket-client` package.
"""
import json
import sys
import threading
import time
import urllib.request

import websocket

SERVER = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8097"
USER = sys.argv[2] if len(sys.argv) > 2 else "admin"
PASSWORD = sys.argv[3] if len(sys.argv) > 3 else "wptest"
DEVICE, DEVICE_ID = "WatchPartyTestTV", "wp-test-tv"
AUTH = f'MediaBrowser Client="WatchPartyFake", Device="{DEVICE}", DeviceId="{DEVICE_ID}", Version="1.0"'


def call(method, path, body=None, token=None):
    headers = {"Content-Type": "application/json", "Authorization": AUTH + (f', Token="{token}"' if token else "")}
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(SERVER + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req) as resp:
        text = resp.read()
        return json.loads(text) if text else None


token = call("POST", "/Users/AuthenticateByName", {"Username": USER, "Pw": PASSWORD})["AccessToken"]
call("POST", "/Sessions/Capabilities/Full", {
    "PlayableMediaTypes": ["Video", "Audio"],
    "SupportedCommands": ["DisplayMessage"],
    "SupportsMediaControl": True,
}, token)


def keepalive(ws):
    while True:
        time.sleep(20)
        ws.send(json.dumps({"MessageType": "KeepAlive"}))


ws_url = SERVER.replace("http", "ws", 1) + f"/socket?api_key={token}&deviceId={DEVICE_ID}"
ws = websocket.create_connection(ws_url)
threading.Thread(target=keepalive, args=(ws,), daemon=True).start()
print(json.dumps({"ready": DEVICE}), flush=True)
while True:
    message = json.loads(ws.recv())
    if message.get("MessageType") in ("Playstate", "Play", "GeneralCommand"):
        print(json.dumps(message), flush=True)
