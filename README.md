# Watch Party for Jellyfin

Let your Discord watch party run the movie night. Friends nominate titles and vote on what to watch, then vote to pause, resume, skip or stop the screen everyone is watching.

- **Runs entirely inside Jellyfin.** The plugin brings its own Discord bot, so there's no separate program, tunnel, port forwarding or HTTPS setup. It works the same on Windows, Linux, macOS and Docker.
- **Outbound only.** The bot connects out to Discord, the way a browser does, so your server never has to be reachable from the internet.
- Requires **Jellyfin 10.11**.

## Setup in 3 steps

**1. Install the plugin.** In Jellyfin, open **Dashboard → Plugins → Repositories**, click **+**, and add:
- Name: `Watch Party`
- URL: `https://raw.githubusercontent.com/<your-github-name>/jellyfin-plugin-watchparty/main/manifest.json`

Then open **Catalog**, click **Watch Party → Install**, and restart Jellyfin.

**2. Make a Discord bot and paste its token.** This takes about 2 minutes:
1. Go to [discord.com/developers/applications](https://discord.com/developers/applications) and click **New Application**. The name you choose becomes the bot's name.
2. On the **Installation** page, set **Install Link** to **None** and click Save Changes.
3. On the **Bot** page, turn off **Public Bot**. Then click **Reset Token** → **Copy**.
4. In Jellyfin, open **Dashboard → Plugins → Watch Party**, paste the token and click **Save**.

**3. Invite the bot.** The settings page shows **Bot connected** and an **Invite** link. Click it, choose your server, then type `/nowplaying` in any channel.

That's it. The bot locks itself to that server, and whatever Jellyfin player is playing becomes the host screen. The settings page has a checklist that turns green as each step is done.

### Optional settings (under "Advanced")
- **Host device:** always control one specific player, instead of whichever is playing.
- **Host role ID:** members with this role can skip votes (`force:true`) and cancel them.
- **Vote length** and **Minimum voters**.
- **Announcement webhook URL:** also posts each vote result to a channel.

## Commands

| Command | What it does |
|---|---|
| `/nominate <title>` | Add a movie or episode to the ballot (autocompletes from your library, max 5) |
| `/ballot` | Show the nominees |
| `/pick` | Vote between the nominees; the winner plays on the host screen |
| `/pause` `/resume` `/skip` `/stop` | Start a yes/no vote. With the host role, add `force:true` to do it straight away |
| `/cancelvote` | Cancel the running vote (host role) |
| `/nowplaying` | Show what's on the host screen |

**Voting rules:**
- One vote runs at a time, for 60 seconds by default.
- Whoever starts a yes/no vote counts as a yes, and the vote passes when yes beats no.
- A pick goes to the title with the most votes; ties are broken at random.
- A vote fails if fewer people than **Minimum voters** take part.
- You can change your vote until the timer ends.
- `/skip` moves to the next item in the queue, or stops playback if nothing is queued.

## Webhook for other tools (optional)

Set a **Webhook secret** in the settings and anything on your network can send commands, such as a Stream Deck, Home Assistant or a script:

```sh
curl -X POST http://YOUR-JELLYFIN:8096/WatchParty/Command \
  -H 'X-WatchParty-Secret: your-secret' -H 'Content-Type: application/json' \
  -d '{"action":"pause","user":"streamdeck"}'
```

| Body | Effect |
|---|---|
| `{"action":"pause"}` | Actions are `pause`, `resume`, `skip`, `stop`, `pick` and `cancel`; this one starts a vote to pause |
| `{"action":"skip","force":true}` | Skip right away, without a vote |
| `{"user":"bob","vote":"yes"}` | Vote in the running vote with `yes`/`no`, or a 0-based option number for picks |

Leave the secret empty to turn the webhook off.

## Development

Requires the .NET SDK (9 or newer) and Podman.

```sh
dotnet test                         # unit tests
./dev/run-test-server.sh --sideload # throwaway Jellyfin 10.11 on http://127.0.0.1:8097 with this build
./dev/serve-local-repo.sh           # local plugin repository to test a real Catalog install
python3 dev/fake-player.py          # a fake remote-controllable "TV" that logs the commands it gets
```

**Releasing:** push a tag such as `v1.0.0`. GitHub Actions tests and builds the plugin, attaches the zip to a release, and updates `manifest.json`, so servers see the update in their Catalog.
