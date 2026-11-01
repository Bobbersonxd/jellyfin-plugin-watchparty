# Watch Party plugin: design spec

Date: 2026-11-01. Revision 2: the Discord link is now a built-in Gateway bot, the plugin is installed from the Jellyfin catalog, and it targets 10.11.

## Goal
A Jellyfin plugin that lets a Discord watch party control **one host Jellyfin session** (the screen being shared in Discord). Picking what to watch, pause/resume, skip and stop are all decided by timed votes. Other tools on the host machine (Stream Deck, Home Assistant, curl) can push the same commands through a secret-protected webhook.

**No extra installation on any OS.** The user adds a repository URL in Jellyfin, clicks Install, and pastes a Discord bot token into the plugin settings. There is no tunnel, port forwarding, reverse proxy, HTTPS setup, sidecar process or file copying. The plugin runs entirely inside Jellyfin on the host machine and only makes outbound connections.

Success means:
- From Discord, friends can nominate titles, vote one in, and it starts on the host screen.
- Mid-movie, `/pause` opens a vote and the host screen pauses once the vote passes.
- A member with the host role can skip any vote.
- Installing works from the Jellyfin catalog on Windows, Linux, macOS and Docker.

Out of scope for v1: SyncPlay groups, Discord voice-channel presence, keeping state across restarts, more than one host session, multiple concurrent votes, and Jellyfin 12.

## Platform and distribution
- Target **Jellyfin 10.11** (`targetAbi 10.11.0.0`). Build against the NuGet packages `Jellyfin.Controller` and `Jellyfin.Model` `10.11.11` on `net9.0`.
- The plugin is pure managed code with **no third-party runtime dependencies**. The Discord gateway and REST client are written on `System.Net.WebSockets.ClientWebSocket`, `HttpClient` and `System.Text.Json`. This avoids clashes with Jellyfin's own assemblies and keeps it working on every OS and architecture.
- Distribution is a Jellyfin plugin repository. The GitHub repo hosts `manifest.json` on GitHub Pages or a raw URL. A GitHub Actions workflow builds a release zip on each tag (`jprm` or `dotnet publish` + zip + MD5 checksum) and updates the manifest. Users add the manifest URL under Dashboard → Plugins → Repositories.

## Integration model
- **Discord ↔ plugin:** a Gateway bot runs inside the plugin as a hosted background service.
  - It connects to `wss://gateway.discord.gg/?v=10&encoding=json`: HELLO, then heartbeat, then IDENTIFY with intents `0`. No privileged intents are needed for interactions.
  - It handles `INTERACTION_CREATE`, and does RESUME or a full reconnect with backoff.
  - It replies through `POST /interactions/{id}/{token}/callback` within 3 s.
- **Outcome messages:** the plugin edits the original vote message with `PATCH /webhooks/{appId}/{token}/messages/@original`. The token lasts 15 minutes, which is longer than any vote. An optional channel webhook URL also posts results and "Now playing".
- **Generic webhook:** `POST /WatchParty/Command`, with header `X-WatchParty-Secret: <secret>` and body `{"action": "pause|resume|skip|stop|pick|cancel", "user": "<id>", "force": false, "vote": "yes|no|<index>"}`. It is served on Jellyfin's own port for local tools. An empty secret turns it off.
- **Bot status:** the settings page shows the bot status (Disconnected, Connecting, Connected as `<name>`, or Invalid token) and an "Invite bot" link built from the application ID, with scopes `bot applications.commands`. Slash commands are registered automatically when the bot connects.

## Voting rules
- One active vote at a time. Starting another while one is open is refused with "A vote is already in progress".
- **Yes/no votes** (pause, resume, skip, stop): the vote runs for `VoteDurationSeconds` (default 60) and passes when `yes > no` and `yes + no >= MinVoters` (default 1).
- **Pick vote:** one option per nominee. When the timer ends, the option with the most votes wins and ties are broken at random. A pick with zero votes, or fewer voters than `MinVoters`, fails. The ballot clears after a successful pick.
- Each user has one vote and can change it; the last click counts.
- The person who starts a yes/no vote is automatically counted as a yes.
- **Override:** a member who has `HostRoleId` (or a generic webhook call with `force: true`) runs the action at once, and can `/cancelvote`.

## Ballot
- `/nominate <title>` autocompletes against movies and episodes in the library (at most 25 results) and adds the title to the ballot.
- Duplicates are not added. The ballot holds at most 5 nominees. `/ballot` shows the nominees. `/pick` needs at least 2.

## Commands
| Command | Effect |
|---|---|
| `/pause` `/resume` `/skip` `/stop` | Start a yes/no vote; host role + `force:true` runs it at once |
| `/nominate <title>` | Add a title to the ballot |
| `/ballot` | Show the nominees |
| `/pick` | Start the pick vote |
| `/cancelvote` | Cancel the active vote (host role) |
| `/nowplaying` | Show what the host is playing |

Skip means the next item in the queue. If nothing is queued, it stops playback.

## Components
| Unit | Responsibility |
|---|---|
| `Plugin`, `PluginConfiguration`, `configPage.html` | Registration and settings: `DiscordBotToken`, `DiscordApplicationId`, `HostRoleId`, `ChannelWebhookUrl`, `HostDeviceName`, `VoteDurationSeconds`, `MinVoters`, `WebhookSecret`; bot status; invite link |
| `Voting/VoteEngine`, `Voting/Ballot` | Pure vote state machine and nominee rules |
| `Playback/HostController`, `Playback/LibrarySearch` | Host session control through `ISessionManager`; library search |
| `Commands/CommandRouter` | Shared "vote or run now" logic and applying outcomes |
| `Discord/GatewayClient` | WebSocket connection lifecycle: heartbeat, identify, resume, reconnect; raises interaction events |
| `Discord/DiscordRest` | Interaction callbacks, message edits, command registration, channel webhook |
| `Discord/InteractionHandler` | Maps an interaction payload to router calls and builds the response JSON |
| `Discord/DiscordBotService` | `IHostedService` that starts and stops the gateway, restarts it when the token changes, and exposes the status |
| `Api/CommandController` | Generic webhook and bot status endpoint |

## Error handling
- Host session not found: no vote is started, and the reply is an ephemeral "Host screen offline". If the host disappears before a vote resolves, the outcome says it could not be applied.
- Gateway: close code 4004 (bad token) sets the status to Invalid token and stops reconnecting until the config changes. Any other disconnect reconnects with exponential backoff (1 s up to 60 s), resuming when possible.
- Discord REST failures are logged and do not affect playback. HTTP 429 honours `retry_after`.
- Generic webhook: an empty secret gives 404, and a wrong secret gives 401.
- State is in memory only.

## Testing
- xUnit: VoteEngine, Ballot, CommandRouter (fake host), InteractionHandler mapping and JSON, GatewayClient protocol logic against a fake socket (heartbeat interval, identify payload, resume after a reconnect opcode, stop on 4004), CommandController.
- Live: a throwaway Jellyfin 10.11.11 Podman container on port 8097. Install through the plugin repository flow (a local manifest served to the container) to prove the catalog install, then drive the generic webhook and a real Discord test server.
