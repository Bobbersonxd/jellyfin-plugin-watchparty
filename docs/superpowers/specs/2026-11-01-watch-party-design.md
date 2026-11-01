# Watch Party plugin: design spec

Date: 2026-11-01. Status: awaiting review.

## Goal
A Jellyfin plugin that lets a Discord watch party control **one host Jellyfin session** (the screen being shared in Discord). Picking what to watch, pause/resume, skip and stop are all decided by timed votes. Other tools (Stream Deck, Home Assistant, curl) can push the same commands in through a secret-protected webhook.

Success means: from Discord, friends can nominate titles, vote one in, and it starts on the host screen. Mid-movie, `/pause` opens a vote and the host screen pauses once the vote passes. A member with the host role can skip any vote.

Out of scope for v1: SyncPlay groups, reading Discord voice-channel presence, keeping state across restarts, more than one host session, and multiple concurrent votes.

## Integration model
- **Discord → plugin:** a Discord application with its **Interactions Endpoint URL** set to `https://<public-host>/WatchParty/Discord`. Discord POSTs slash commands, autocomplete requests and button clicks there. Jellyfin must be reachable over public HTTPS, for example through a Cloudflare Tunnel.
- **Plugin → Discord:** the plugin edits the original vote message through `PATCH /webhooks/{appId}/{interactionToken}/messages/@original`; the token is valid for 15 minutes, which is longer than any vote. An optional channel webhook URL posts "Now playing" and vote results.
- **Generic webhook:** `POST /WatchParty/Command`, with header `X-WatchParty-Secret: <secret>` and body `{"action": "pause|resume|skip|stop|pick|cancel", "user": "<id>", "force": false, "vote": "yes|no|<index>"}`. A body carrying `vote` casts a ballot in the active vote; any other body starts a vote or runs the action.

## Voting rules
- One active vote at a time. Starting another while one is open is refused with "vote in progress".
- **Yes/no votes** (pause, resume, skip, stop): the vote runs for `VoteDurationSeconds` (default 60) and passes when `yes > no` and `yes + no >= MinVoters` (default 1).
- **Pick vote:** one option per nominee. When the timer ends, the option with the most votes wins and ties are broken at random. A pick with zero votes, or fewer voters than `MinVoters`, fails. The ballot clears after a successful pick.
- Each user has one vote and can change it; the last click counts.
- The person who starts a yes/no vote is automatically counted as a yes.
- **Override:** a Discord member who has `HostRoleId` (or a generic webhook call with `force: true`) runs the action at once, and can `/cancelvote`.
- Timer resolution uses an injected clock/scheduler so the engine is unit-testable.

## Ballot
- `/nominate <title>` autocompletes against movies and episodes in the Jellyfin library (name search, at most 25 results, which is Discord's limit). The value is the item ID.
- Duplicate titles are not added. The ballot holds at most 5 nominees, the number of buttons in one Discord row.
- `/ballot` shows the current nominees. `/pick` needs at least 2 of them.

## Commands
| Command | Effect |
|---|---|
| `/pause` `/resume` `/skip` `/stop` | Start a yes/no vote; host role or `force:true` runs it at once |
| `/nominate <title>` | Add a title to the ballot |
| `/ballot` | Show the nominees |
| `/pick` | Start the pick vote |
| `/cancelvote` | Cancel the active vote (host role only) |
| `/nowplaying` | Show what the host is playing |

Skip means "next item in the queue" (the next episode). If nothing is queued, it stops playback.

## Components
| Unit | Responsibility |
|---|---|
| `Plugin`, `PluginConfiguration`, `Configuration/configPage.html` | Registration and settings: `DiscordPublicKey`, `DiscordApplicationId`, `DiscordBotToken` (used only to register commands), `HostRoleId`, `ChannelWebhookUrl`, `HostDeviceName`, `VoteDurationSeconds`, `MinVoters`, `WebhookSecret` |
| `Voting/VoteEngine` | Pure vote state machine: start, cast, tally, resolve, cancel; raises a `VoteResolved` event |
| `Voting/Ballot` | Nominee list rules |
| `Playback/HostController` | Finds the host session by device name through `ISessionManager`, sends playstate and play commands, reports what is playing |
| `Discord/SignatureVerifier` | Ed25519 check of `timestamp + body` against the public key (BouncyCastle) |
| `Discord/InteractionsController` | Handles PING, commands, autocomplete and buttons; replies within Discord's 3 s limit |
| `Discord/DiscordClient` | Edits the original message, posts to the channel webhook, registers slash commands |
| `Api/CommandController` | The generic webhook |
| `Commands/CommandRouter` | Shared "vote or run now" logic used by both controllers |

## Error handling
- Host session not found: no vote is started, and the reply is an ephemeral "Host screen offline". If the host disappears before a vote resolves, the result says it could not be applied.
- Bad Discord signature: 401. Bad webhook secret: 401. An empty secret in the config turns the generic webhook off.
- Discord API failures are logged and do not affect playback.
- State is in memory only, so a restart drops the active vote and the ballot.

## Testing
- xUnit: VoteEngine (majority, ties, minimum voters, changed votes, auto-yes for the starter, timeout, cancel), Ballot (duplicates, cap), SignatureVerifier (known-good and tampered signatures), CommandRouter with a fake host.
- Manual: deploy to the local Jellyfin and drive the generic webhook with curl against a real playing client, then the Discord flow through a tunnel.
