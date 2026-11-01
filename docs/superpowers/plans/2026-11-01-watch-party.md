# Watch Party Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Jellyfin 10.11 plugin, installed from the plugin catalog, that runs a built-in Discord bot so a watch party can control one host session through timed votes, with no extra software on any OS.

**Architecture:** Two pure, unit-tested parts, `VoteEngine` and `Ballot`, sit behind a `CommandRouter`. The router decides whether to start a vote or run an action now, and it applies results through `IHostController`, a wrapper over `ISessionManager`. A hosted `DiscordBotService` runs a hand-written outbound Discord Gateway client (`ClientWebSocket`) and feeds interactions through `InteractionHandler` into the router. A generic secret-header webhook controller feeds the same router. A GitHub Actions workflow publishes release zips plus `manifest.json` for the Jellyfin catalog.

**Tech Stack:** C# / net9.0, NuGet `Jellyfin.Controller` + `Jellyfin.Model` 10.11.11, ASP.NET Core (FrameworkReference), System.Text.Json, xUnit, Microsoft.Extensions.TimeProvider.Testing. **No third-party runtime packages.**

**Spec:** `docs/superpowers/specs/2026-11-01-watch-party-design.md` (revision 2)

## Global Constraints
- Target `net9.0`, `targetAbi 10.11.0.0`. Jellyfin packages use `<PackageReference ... ExcludeAssets="runtime" />` in the plugin, and normal references in the tests.
- The plugin's release zip contains **only** `Jellyfin.Plugin.WatchParty.dll` (plus `meta.json`, which Jellyfin writes itself). No other runtime packages.
- Root namespace and assembly: `Jellyfin.Plugin.WatchParty`. Plugin GUID: `6f1c8a52-3d0e-4b7a-9c41-2e5d8f7a1b90`. Name: `Watch Party`.
- Outbound-only networking for Discord: gateway `wss://gateway.discord.gg/?v=10&encoding=json`, REST `https://discord.com/api/v10`, intents `0`. Never require an inbound public URL.
- Routes: `POST /WatchParty/Command` (`[AllowAnonymous]`, secret header) and `GET /WatchParty/Status` (`[Authorize(Policy = "RequiresElevation")]`).
- Defaults: `VoteDurationSeconds = 60`, `MinVoters = 1`. The ballot holds at most `5` nominees. Autocomplete returns at most `25` choices, with names of at most 100 chars.
- Yes/no passes when `yes > no && yes + no >= MinVoters`. The starter is auto-counted as `yes`.
- An interaction must be answered via REST callback within 3 s: compute the response synchronously from in-memory state, and never await playback or other REST calls first.
- Exact strings: `"Host screen offline"`, `"A vote is already in progress"`, `"This vote has ended"`, `"Only the host can cancel votes"`.
- Dev testing uses a throwaway Podman container `docker.io/jellyfin/jellyfin:10.11.11` on port 8097. Never touch the user's 12.0 system service.

## Review Focus
- **Host leaves mid-vote:** expect "passed but could not be applied: Host screen offline" and no exception. Covered by `Router_PassedVote_HostGoneAtResolve_ReportsNotApplied` (Task 3).
- **Click on an old vote message:** expect an ephemeral "This vote has ended", with the current vote untouched. Covered by `Cast_StaleVoteId_ReturnsNoActiveVote` (Task 2) and `Button_StaleVote_EphemeralEnded` (Task 5).
- **Wi-Fi drop or PC sleep:** the socket dies silently. A missed heartbeat ACK must trigger a reconnect with resume, and the bot comes back without a Jellyfin restart. Covered by `MissedHeartbeatAck_Reconnects_WithResume` (Task 4).
- **Wrong or revoked token:** close code 4004. Expect status `InvalidToken`, no reconnect loop that spams Discord, and a restart once the token changes in the config. Covered by `Close4004_StopsAndReportsInvalidToken` (Task 4) and `TokenChange_RestartsGateway` (Task 6).
- **Concurrent clicks:** every vote is counted. Covered by `Cast_ParallelUsers_AllCounted` (Task 2).

---

## File Structure
```
Jellyfin.Plugin.WatchParty.sln   build.yaml   manifest.json   .github/workflows/release.yml
src/Jellyfin.Plugin.WatchParty/
  Jellyfin.Plugin.WatchParty.csproj
  Plugin.cs  PluginServiceRegistrator.cs
  Configuration/PluginConfiguration.cs  Configuration/configPage.html
  Voting/VoteTypes.cs  Voting/VoteEngine.cs  Voting/Ballot.cs
  Playback/IHostController.cs  Playback/HostController.cs  Playback/ILibrarySearch.cs  Playback/LibrarySearch.cs
  Commands/CommandRouter.cs
  Discord/IGatewaySocket.cs         # socket abstraction + ClientWebSocket implementation
  Discord/GatewayClient.cs          # protocol: hello/heartbeat/identify/resume/reconnect
  Discord/DiscordRest.cs            # interaction callback, edit original, register commands, channel webhook
  Discord/CommandDefinitions.cs
  Discord/InteractionHandler.cs     # interaction JSON -> router -> response JSON
  Discord/DiscordBotService.cs      # IHostedService, status, token-change restart, outcome edits
  Api/CommandController.cs
tests/Jellyfin.Plugin.WatchParty.Tests/  (one *Tests.cs per unit, Fakes/)
dev/run-test-server.sh   dev/serve-local-repo.sh
README.md
```

---

### Task 1: Scaffold, plugin shell, config, load in a 10.11 container

**Files:** sln; both csproj files; `build.yaml`; `Plugin.cs`; `PluginServiceRegistrator.cs` (empty body); `Configuration/PluginConfiguration.cs`; placeholder `configPage.html`; `.gitignore` (bin, obj, `.devserver/`); `dev/run-test-server.sh`.

**Produces:**
- `PluginConfiguration : BasePluginConfiguration`. Strings `DiscordBotToken`, `HostRoleId`, `ChannelWebhookUrl`, `HostDeviceName` and `WebhookSecret` default to `""`. `VoteDurationSeconds = 60`, `MinVoters = 1`.
- `Plugin.Instance`, and `Plugin.ConfigurationChanged`, the base event raised on save.
- `GetPages()` returns `{ Name = "WatchParty", EmbeddedResourcePath = "Jellyfin.Plugin.WatchParty.Configuration.configPage.html" }`.
- `DiscordApplicationId` is not a setting: it is learned from the gateway READY event (Task 4).
- `build.yaml` fields: `name: "Watch Party"`, the guid, `targetAbi: "10.11.0.0"`, `framework: "net9.0"`, `artifacts: ["Jellyfin.Plugin.WatchParty.dll"]`, `category: "General"`, `overview` and `description`.
- `dev/run-test-server.sh` (re)creates the container `wp-test`: `podman run -d --name wp-test -p 8097:8096 -v ./.devserver/config:/config:Z -v ./.devserver/media:/media:Z docker.io/jellyfin/jellyfin:10.11.11`. With `--sideload`, it also copies the built DLL into `.devserver/config/plugins/WatchParty_1.0.0.0/` before starting.

- [ ] **Step 1:** Create the projects. Run `dotnet build -c Release`. Expected: 0 errors, and `bin/Release/net9.0/` contains `Jellyfin.Plugin.WatchParty.dll` and no Jellyfin or MediaBrowser DLLs.
- [ ] **Step 2:** Run `dev/run-test-server.sh --sideload` and finish the startup wizard at `http://localhost:8097` (an admin user; one small media folder in `.devserver/media`). Expected: `podman logs wp-test | grep "Watch Party"` shows the plugin loaded, and it is listed in Dashboard → Plugins. Side-loading is a dev shortcut only; the catalog install is proven in Task 7.
- [ ] **Step 3:** Commit: `chore: scaffold Watch Party plugin for 10.11`.

### Task 2: Ballot and VoteEngine (pure, TDD)

**Files:** `Voting/VoteTypes.cs`, `Voting/VoteEngine.cs`, `Voting/Ballot.cs`; tests in `BallotTests.cs` and `VoteEngineTests.cs`.

**Interfaces (Produces):**
```csharp
public enum VoteAction { Pause, Resume, Skip, Stop, Pick }
public sealed record VoteOption(string Key, string Label);           // yes/no keys are "yes"/"no"; pick keys are item Guid "N" strings
public sealed record VoteSettings(int DurationSeconds, int MinVoters);
public sealed class Vote {                                            // immutable except tallies (engine-internal)
  public Guid Id { get; } public VoteAction Action { get; } public IReadOnlyList<VoteOption> Options { get; }
  public string StartedBy { get; } public DateTimeOffset EndsAt { get; } public string? DiscordToken { get; }
  public IReadOnlyDictionary<string,int> Counts { get; }              // option key -> count (snapshot)
}
public sealed record VoteResult(Vote Vote, bool Passed, VoteOption? Winner, string Reason);
public enum CastStatus { Counted, Changed, NoActiveVote, UnknownOption }
public sealed class VoteEngine {
  public VoteEngine(TimeProvider time, Random random, Func<VoteSettings> settings);
  public Vote? Active { get; }
  public Vote? TryStart(VoteAction action, IReadOnlyList<VoteOption> options, string startedBy, string? discordToken); // null if one is active
  public CastStatus Cast(Guid voteId, string user, string optionKey);
  public VoteResult? Cancel();                                         // null if none active
  public event Action<VoteResult>? Resolved;                           // raised on timer expiry and on Cancel
}
public sealed record Nominee(Guid ItemId, string Title);
public enum AddStatus { Added, Duplicate, Full }
public sealed class Ballot { public const int MaxNominees = 5; public IReadOnlyList<Nominee> Nominees { get; }
  public AddStatus Add(Guid itemId, string title); public void Clear(); }
public static class VoteOptions { public static IReadOnlyList<VoteOption> YesNo { get; } }  // [("yes","Yes"),("no","No")]
```
Engine rules:
- For a yes/no action (anything but `Pick`), `TryStart` records the starter as "yes".
- The timer is `time.CreateTimer(..., TimeSpan.FromSeconds(settings().DurationSeconds), Timeout.InfiniteTimeSpan)`. On expiry, the vote is resolved and `Active` becomes null *before* `Resolved` is raised, so a handler can start a new vote.
- Reasons: `"passed"`, `"rejected"`, `"not enough voters"`, `"cancelled"`, `"no votes"`.
- In a pick, ties go to `random.Next(tied.Count)` over the tied options in option order.
- One `lock` object guards all state.

- [ ] **Step 1: Ballot tests.** `Add_FirstItem_Added`; `Add_SameItemTwice_Duplicate` (Nominees.Count stays 1); `Add_SixthItem_Full`; `Clear_Empties`.
- [ ] **Step 2:** Run `dotnet test --filter BallotTests`. Expected: FAIL (compile errors).
- [ ] **Step 3:** Implement `Ballot`. Run the filter again. Expected: PASS.
- [ ] **Step 4: VoteEngine tests**, built on `FakeTimeProvider` and `new Random(1)`, with settings `(60, 1)` unless a test says otherwise:
  - `TryStart_YesNo_StarterCountedYes`: `Counts["yes"] == 1`.
  - `TryStart_WhileActive_ReturnsNull`.
  - `Cast_ChangeVote_ReturnsChanged_AndMovesCount`: user b votes yes, then no, giving yes=1 (the starter) and no=1.
  - `Cast_UnknownOption_ReturnsUnknownOption`.
  - `Cast_StaleVoteId_ReturnsNoActiveVote`: let vote 1 resolve, start vote 2, cast with vote 1's ID. The result is `NoActiveVote`, and vote 2's counts are unchanged.
  - `Timer_YesMajority_Passes`: advance 60 s. `Resolved` fires once with `Passed=true` and `Reason="passed"`, and `Active == null`.
  - `Timer_Tie_Rejected`: starter yes, b no, so `Passed=false` and `"rejected"`.
  - `Timer_BelowMinVoters_Fails`: settings `(60, 3)` with only the starter's vote gives `"not enough voters"`.
  - `Timer_NotFiredEarly`: advance 59 s. `Resolved` has not fired.
  - `Pick_MostVotesWins`: three options with votes 2/1/0, so the Winner key is the first option.
  - `Pick_Tie_UsesRandom`: with seeded Random(1), the expected winner is whatever `new Random(1).Next(2)` picks.
  - `Pick_NoVotes_Fails_NoVotesReason`.
  - `Cancel_RaisesResolvedCancelled_AndClearsActive`.
  - `Resolved_HandlerCanStartNewVote`: inside the handler, `TryStart` returns non-null.
  - `Cast_ParallelUsers_AllCounted`: `Parallel.For(0, 200, i => Cast(id, $"u{i}", "yes"))` gives `Counts["yes"] == 201`.
- [ ] **Step 5:** Run `dotnet test --filter VoteEngineTests`. Expected: FAIL.
- [ ] **Step 6:** Implement the types and the engine.
- [ ] **Step 7:** Run `dotnet test`. Expected: all pass.
- [ ] **Step 8:** Commit: `feat: vote engine and ballot`.

### Task 3: Host controller, library search, command router

**Files:** `Playback/IHostController.cs`, `Playback/HostController.cs`, `Playback/ILibrarySearch.cs`, `Playback/LibrarySearch.cs`, `Commands/CommandRouter.cs`; tests `CommandRouterTests.cs`, `Fakes/FakeHost.cs`.

**Interfaces:**
```csharp
public sealed record HostStatus(bool Online, string? NowPlayingTitle, bool HasNextInQueue);
public interface IHostController { HostStatus GetStatus(); Task<bool> SendAsync(VoteAction action, CancellationToken ct); Task<bool> PlayAsync(Guid itemId, CancellationToken ct); }
public interface ILibrarySearch { IReadOnlyList<Nominee> Search(string query, int limit); }
public enum ReplyKind { VoteStarted, Done, Info, Error }               // Error => ephemeral on Discord
public sealed record RouterReply(ReplyKind Kind, string Message, Vote? Vote = null);
public sealed record VoteOutcome(VoteResult Result, bool Applied, string Message);
public sealed class CommandRouter {
  public CommandRouter(VoteEngine engine, Ballot ballot, IHostController host, ILogger<CommandRouter> log);
  public Task<RouterReply> RequestAsync(VoteAction action, string user, bool privileged, string? discordToken, CancellationToken ct);
  public RouterReply Cast(Guid voteId, string user, string optionKey);
  public RouterReply Cancel(bool privileged);
  public RouterReply Nominate(Guid itemId, string title);
  public RouterReply ShowBallot();
  public RouterReply NowPlaying();
  public event Func<VoteOutcome, Task>? Completed;                     // raised after a resolved vote is applied (or not)
}
```
`HostController` (uses `ISessionManager`, `ILibraryManager` and plugin config):
- The host is the first entry in `ISessionManager.Sessions` where `DeviceName` equals `HostDeviceName` (OrdinalIgnoreCase) and `SupportsRemoteControl` is true.
- `SendAsync` maps Pause→`Pause`, Resume→`Unpause`, Skip→`NextTrack`, Stop→`Stop`, and calls `SendPlaystateCommand(null, session.Id, new PlaystateRequest{Command=…}, ct)`. A null controlling session skips the control check; confirm this against the 10.11.11 package (the signature is `SendPlaystateCommand(string? controllingSessionId, string sessionId, PlaystateRequest, CancellationToken)`).
- `PlayAsync` returns false if `GetItemById(id)` is null. Otherwise it calls `SendPlayCommand(null, session.Id, new PlayRequest{ ItemIds=[id], PlayCommand=PlayCommand.PlayNow }, ct)`.
- `HasNextInQueue`: the index of the item whose `PlaylistItemId` matches in `NowPlayingQueue` is less than `Count-1`.
- Catch exceptions, log them, and return false.

`LibrarySearch` calls `GetItemList(new InternalItemsQuery{ SearchTerm=query, IncludeItemTypes=[BaseItemKind.Movie, BaseItemKind.Episode], Limit=limit, Recursive=true })`. Episodes get the label `"{SeriesName} S{season:00}E{ep:00} {Name}"`.

Router rules:
- `RequestAsync`: host offline gives Error "Host screen offline". If privileged, run now and return `Done "<user> forced <action>"`. `Pick` needs at least 2 nominees (Error otherwise), and its options are the nominees. An active vote gives Error "A vote is already in progress". Otherwise the result is VoteStarted.
- Skip with `!HasNextInQueue` sends Stop instead.
- On `engine.Resolved`: if the vote passed, apply it. For Pick, call `PlayAsync(winner)` and `ballot.Clear()` only on success. Then raise `Completed`. The message is "passed", "rejected" etc., or `"passed but could not be applied: Host screen offline"`.
- `Cancel` without the privilege gives Error "Only the host can cancel votes".

- [ ] **Step 1: Tests** with `FakeHost` (settable `HostStatus`, a list of recorded actions, a settable PlayAsync result) and `FakeTimeProvider`:
  - `Request_HostOffline_ErrorNoVote`
  - `Request_Privileged_RunsImmediately`
  - `Request_StartsVote`
  - `Request_WhileActive_Error`
  - `PassedPauseVote_SendsPause_RaisesCompletedApplied`
  - `Skip_NoNextInQueue_SendsStop`
  - `Pick_FewerThanTwoNominees_Error`
  - `PassedPick_PlaysWinner_ClearsBallot`
  - `Router_PickWinnerUnplayable_KeepsBallot`
  - `Router_PassedVote_HostGoneAtResolve_ReportsNotApplied`: set Online=false before the timer fires. `Applied=false`, and the message contains "Host screen offline".
  - `Cancel_NotPrivileged_Error`
- [ ] **Step 2:** Run the tests. Expected: FAIL. Implement the router, `HostController` and `LibrarySearch`, then run `dotnet test`. Expected: all pass. HostController and LibrarySearch are checked against the live server in Task 6.
- [ ] **Step 3:** Register singletons in `PluginServiceRegistrator`: `TimeProvider.System`; `VoteEngine`, whose settings delegate reads `Plugin.Instance.Configuration`; `Ballot`; `IHostController`; `ILibrarySearch`; `CommandRouter`. Run `dotnet build`. Expected: 0 errors.
- [ ] **Step 4:** Commit: `feat: host control and command router`.

### Task 4: Discord Gateway client (outbound WebSocket)

**Files:** `Discord/IGatewaySocket.cs`, `Discord/GatewayClient.cs`; tests `GatewayClientTests.cs`, `Fakes/FakeGatewaySocket.cs`.

**Interfaces (Produces):**
```csharp
public abstract record GatewayFrame { public sealed record Text(string Json) : GatewayFrame; public sealed record Closed(int? Code) : GatewayFrame; }
public interface IGatewaySocket : IAsyncDisposable {
  Task ConnectAsync(Uri uri, CancellationToken ct); Task SendAsync(string json, CancellationToken ct);
  Task<GatewayFrame> ReceiveAsync(CancellationToken ct);              // reassembles multi-frame text messages
  Task CloseAsync(int code, CancellationToken ct); }
public sealed class ClientWebSocketGatewaySocket : IGatewaySocket { }  // System.Net.WebSockets.ClientWebSocket
public enum BotState { Disconnected, Connecting, Connected, InvalidToken }
public sealed class GatewayClient {
  public GatewayClient(Func<IGatewaySocket> socketFactory, TimeProvider time, Random random, ILogger<GatewayClient> log);
  public BotState State { get; } public string? BotUserName { get; } public string? ApplicationId { get; }
  public event Func<JsonElement, Task>? InteractionCreated;            // the "d" of INTERACTION_CREATE
  public event Func<Task>? Ready;                                     // after READY (used to register commands)
  public Task RunAsync(string token, CancellationToken ct);           // runs until ct cancelled or InvalidToken
}
```
Protocol rules (opcodes 0 Dispatch, 1 Heartbeat, 2 Identify, 6 Resume, 7 Reconnect, 9 Invalid Session, 10 Hello, 11 Heartbeat ACK):
- **Heartbeat:** on HELLO, start heartbeating every `heartbeat_interval` ms; the first beat comes after `interval * random.NextDouble()`. The payload is `{"op":1,"d":<last seq or null>}`. An op 1 from the server gets an immediate beat. If no ACK arrives before the next beat, close the socket (code 4000) and reconnect with resume.
- **Identify:** `{"op":2,"d":{"token":T,"intents":0,"properties":{"os":<RuntimeInformation.OSDescription>,"browser":"jellyfin-watchparty","device":"jellyfin-watchparty"}}}`.
- **READY:** store `session_id`, `resume_gateway_url`, `user.username` and `application.id`. Set `State=Connected` and raise `Ready`.
- **Resume:** reconnect to `resume_gateway_url` + `?v=10&encoding=json`, then send `{"op":6,"d":{"token","session_id","seq"}}`. Op 9 with `d:false` clears the session and identifies again after waiting 1–5 s.
- **Op 7:** reconnect with resume.
- **Close codes:** 4004 sets `State=InvalidToken` and returns from `RunAsync`. 4010–4014 are logged as fatal and return with `State=Disconnected`. Anything else reconnects after a backoff of 1, 2, 4… seconds up to 60 s, which resets to 1 s after a READY or RESUMED.
- Track `s` from every dispatch.
- Exceptions in event handlers are logged and never break the receive loop.

- [ ] **Step 1: Tests.** `FakeGatewaySocket` takes scripted frames into a channel and records what is sent; `FakeTimeProvider` drives timing.
  - `Hello_SendsIdentify_WithIntentsZero`
  - `Hello_SchedulesHeartbeat_WithLastSeq`
  - `ServerOp1_SendsImmediateHeartbeat`
  - `Ready_SetsConnected_AndApplicationId`
  - `Dispatch_InteractionCreate_RaisesEvent`
  - `MissedHeartbeatAck_Reconnects_WithResume`: the second socket receives op 6 with the stored session_id and seq.
  - `Op7_Reconnects_WithResume`
  - `Op9False_ReIdentifies`
  - `Close4004_StopsAndReportsInvalidToken`: RunAsync completes, and only one socket was created.
  - `OtherClose_ReconnectsWithBackoff`
  - `HandlerThrows_LoopContinues`
- [ ] **Step 2:** Run `dotnet test --filter GatewayClientTests`. Expected: FAIL. Implement both files, then run the filter again. Expected: PASS.
- [ ] **Step 3:** Commit: `feat: outbound discord gateway client`.

### Task 5: Interaction handling and Discord REST

**Files:** `Discord/DiscordRest.cs`, `Discord/CommandDefinitions.cs`, `Discord/InteractionHandler.cs`; tests `InteractionHandlerTests.cs`, `DiscordRestTests.cs` (with a fake `HttpMessageHandler`).

**Interfaces:**
```csharp
public sealed class DiscordRest {
  public DiscordRest(IHttpClientFactory http, ILogger<DiscordRest> log);
  public Task<bool> RespondAsync(string interactionId, string token, JsonObject response, CancellationToken ct);       // POST /interactions/{id}/{token}/callback
  public Task<bool> EditOriginalAsync(string appId, string token, JsonObject message, CancellationToken ct);           // PATCH /webhooks/{app}/{token}/messages/@original
  public Task<bool> RegisterCommandsAsync(string appId, string botToken, JsonArray commands, CancellationToken ct);    // PUT /applications/{app}/commands, "Authorization: Bot {token}"
  public Task<bool> PostChannelWebhookAsync(string url, string content, CancellationToken ct);
}   // all: non-2xx -> log + false; 429 -> wait retry_after (cap 5 s) and retry once
public static class CommandDefinitions { public static JsonArray All { get; } }
public sealed class InteractionHandler {
  public InteractionHandler(CommandRouter router, ILibrarySearch search, ILibraryManager library, Func<PluginConfiguration> config);
  public Task<JsonObject> HandleAsync(JsonElement interaction, CancellationToken ct);  // returns the callback body
  public static JsonObject VoteMessage(Vote v, int type);           // 4 = new message, 7 = update
  public static JsonObject OutcomeMessage(VoteOutcome o);           // final tally + message, components: []
  public static bool TryParseCustomId(string id, out Guid voteId, out string optionKey);  // "wp:{voteId:N}:{key}"
}
```
- **Mapping:**
  - Type 2 (command) is routed by `data.name`. Type 3 (button) parses the custom_id, then calls `router.Cast`; `Counted`/`Changed` reply with `VoteMessage(type 7)` and `NoActiveVote` with an ephemeral "This vote has ended". Type 4 (autocomplete) replies type 8 with choices from `search.Search(value, 25)`.
  - The user is `member.user.id`. When `member` is missing (a DM), reply ephemeral "Use this in a server".
  - `privileged` = `member.roles` contains `HostRoleId` && (`force` option true || the command is `cancelvote`).
- **Router replies:** `VoteStarted` gives `VoteMessage(type 4)`, with the interaction `token` passed as `discordToken`. `Error` gives an ephemeral reply (`flags: 64`), and Done/Info a public message (type 4).
- **Vote message:** the content is `"**Vote: {action}** — ends <t:{EndsAt unix}:R>"` plus one `"{Label}: {count}"` line per option. It has one action row with one button per option (style 1, `custom_id` `wp:{id:N}:{key}`, label `"{Label} ({count})"`, at most 80 chars).
- **CommandDefinitions:** pause, resume, skip and stop each take an optional boolean `force`; nominate takes a required string `title` with `autocomplete:true`; then ballot, pick, cancelvote and nowplaying. All have `dm_permission:false`.

- [ ] **Step 1: Tests.**
  - `Command_Pause_StartsVote_ReturnsVoteMessageWithButtons`
  - `Command_ForceWithoutRole_StartsVoteInstead`
  - `Command_ForceWithRole_RunsImmediately`
  - `Button_Yes_ReturnsType7UpdatedCount`
  - `Button_StaleVote_EphemeralEnded`
  - `Autocomplete_ReturnsType8_Max25_NamesTruncated100`
  - `Nominate_AddsToBallot`
  - `Dm_ReturnsEphemeralUseInServer`
  - `ParseCustomId_RoundTrips`
  - `OutcomeMessage_HasNoComponents`
  - In `DiscordRestTests`: `Rest_429_RetriesOnceAfterRetryAfter`, `Rest_500_ReturnsFalse`, and `Register_UsesBotAuthHeader_AndPut`.
- [ ] **Step 2:** Run the tests. Expected: FAIL. Implement, then run `dotnet test`. Expected: all pass.
- [ ] **Step 3:** Commit: `feat: discord interaction handling and rest`.

### Task 6: Bot service, generic webhook, status, live end-to-end

**Files:** `Discord/DiscordBotService.cs`, `Api/CommandController.cs`; update `PluginServiceRegistrator.cs`; tests `DiscordBotServiceTests.cs`, `CommandControllerTests.cs`.

**Interfaces:**
- `DiscordBotService : IHostedService`, exposing `BotState State`, `string? BotUserName` and `string? ApplicationId`.
  - **Start:** if `DiscordBotToken` is non-empty, run `GatewayClient.RunAsync` on a background task.
  - **Token change:** on `Plugin.Instance.ConfigurationChanged`, if the token changed, cancel and restart the gateway.
  - **Wiring:** `InteractionCreated` → `InteractionHandler.HandleAsync` → `DiscordRest.RespondAsync`. `Ready` → `RegisterCommandsAsync`. `router.Completed` → `EditOriginalAsync` when the vote has a DiscordToken and ApplicationId is known, plus `PostChannelWebhookAsync` when `ChannelWebhookUrl` is set.
  - **Stop:** cancel the gateway and await it, with a 5 s timeout.
- `CommandController`:
  - `POST /WatchParty/Command` uses the body `CommandRequest { string Action; string User; bool Force; string? Vote; Guid? VoteId }` and `X-WatchParty-Secret`, compared with `CryptographicOperations.FixedTimeEquals`.
  - An empty config secret gives 404, a wrong secret 401, and an unknown action 400.
  - If `Vote` is set, the request calls `router.Cast(VoteId ?? Active.Id, User, key)`, where `key` is `Vote`, or `Active.Options[int]` when `Vote` parses as an int (out of range gives 400).
  - The response is `{kind, message, voteId?}`: 200, or 409 when the router returns Error.
  - `GET /WatchParty/Status` (admin) returns `{state, botUserName, applicationId, inviteUrl}`, where `inviteUrl` = `https://discord.com/oauth2/authorize?client_id={appId}&scope=bot%20applications.commands&permissions=0`, or null when the app ID is unknown.
- `PluginServiceRegistrator` registers:
  - `HttpClient`, plus singletons for `GatewayClient`, with factory `() => new ClientWebSocketGatewaySocket()`, `DiscordRest` and `InteractionHandler`;
  - `DiscordBotService`, as a singleton and also through `AddHostedService(sp => sp.GetRequiredService<DiscordBotService>())`.

- [ ] **Step 1: Tests.**
  - `NoToken_DoesNotStartGateway`
  - `TokenChange_RestartsGateway`, using a fake gateway runner `Func<string, CancellationToken, Task>` injected in an internal ctor
  - `Completed_WithToken_EditsOriginal`
  - Controller: `EmptySecret_404`, `WrongSecret_401`, `UnknownAction_400`, `ValidPause_StartsVote_200`, `VoteIndex_MapsToOptionKey`, `VoteIndexOutOfRange_400`, `Status_ReturnsInviteUrlWhenAppIdKnown`
- [ ] **Step 2:** Run the tests. Expected: FAIL. Implement, then run `dotnet test`. Expected: all pass.
- [ ] **Step 3: Live webhook check.** Side-load the build into `wp-test`.
  1. Set `WebhookSecret=test`, `HostDeviceName=<browser device name>` and `VoteDurationSeconds=15` with `curl` to `/Plugins/{guid}/Configuration` using an admin API key.
  2. Play a movie in a browser at `localhost:8097`.
  3. Run `curl -s -XPOST localhost:8097/WatchParty/Command -H 'X-WatchParty-Secret: test' -H 'Content-Type: application/json' -d '{"action":"pause","user":"a"}'`.

  Expected: VoteStarted, then the player pauses about 15 s later. `"force":true` with `resume` resumes it at once.
- [ ] **Step 4: Live Discord check (Bob).**
  1. Bob creates an app in the Discord Developer Portal → Bot, copies the token and pastes it into the config.
  2. `GET /WatchParty/Status` shows `Connected`; Bob opens the invite URL and adds the bot to a test server.
  3. In Discord: `/nominate` autocompletes from the library, `/pick` buttons count up, and when the vote ends the message edits to the result and the winner plays.
  4. Unplug the network for 2 minutes, then plug it back in. Expected: the status returns to Connected without a Jellyfin restart.
- [ ] **Step 5:** Commit: `feat: discord bot service and command webhook`.

### Task 7: Config page, catalog packaging, CI release, README

**Files:** `Configuration/configPage.html`, `.github/workflows/release.yml`, `manifest.json`, `dev/serve-local-repo.sh`, `README.md`.

- [ ] **Step 1: Config page.** Use the standard Jellyfin plugin page (`data-role="page"`, `ApiClient.getPluginConfiguration` / `updatePluginConfiguration`).
  - Fields for every config value. The token and secret are `type=password`.
  - The host device is a `<select>` filled from `ApiClient.getSessions()` (sessions that support remote control), with a free-text fallback.
  - A status panel polls `ApiClient.getJSON(ApiClient.getUrl('WatchParty/Status'))` every 5 s and shows the state, the bot name, and an "Invite bot to your server" link when `inviteUrl` is set.
  - Step-by-step help text for getting a bot token, with a link to `https://discord.com/developers/applications`.
- [ ] **Step 2: Packaging script.** `dev/serve-local-repo.sh` does three things:
  1. Builds a Release zip containing only `Jellyfin.Plugin.WatchParty.dll`.
  2. Writes `.devserver/repo/manifest.json` in Jellyfin's repository format: `[{guid, name, overview, description, owner, category, versions:[{version:"1.0.0.0", changelog, targetAbi:"10.11.0.0", sourceUrl, checksum:<MD5 hex of zip>, timestamp}]}]`, where `sourceUrl` = `http://host.containers.internal:8765/watchparty_1.0.0.0.zip`.
  3. Serves `.devserver/repo` with `python3 -m http.server 8765`.
- [ ] **Step 3: Prove the catalog install.**
  1. Recreate `wp-test` without `--sideload`.
  2. Add the repository `http://host.containers.internal:8765/manifest.json` under Dashboard → Plugins → Repositories, install Watch Party from the Catalog, and restart.

  Expected: the plugin is Active and the settings page opens. Copying no files by hand is the "no extra installation" proof.
- [ ] **Step 4: CI.** `release.yml`: on a `v*` tag, use `actions/setup-dotnet` 9.0.x, run `dotnet test`, build the zip, compute its MD5, and create a GitHub Release with the zip attached. Then prepend the version entry to `manifest.json` on `main`, with `sourceUrl` pointing at the release asset, and commit it. Users add `https://raw.githubusercontent.com/<owner>/jellyfin-plugin-watchparty/main/manifest.json`. Creating the GitHub repo and pushing are Bob's call; ask before doing either.
- [ ] **Step 5: README.**
  - Install: add the repository URL, then install from the Catalog.
  - Make a Discord bot: portal → New Application → Bot → Reset Token → paste.
  - Use the invite link from the settings page.
  - Pick the host device.
  - The command list, voting rules, and a generic webhook curl example.
  - A note that nothing needs to be exposed to the internet.
- [ ] **Step 6:** Run `dotnet test`. Expected: all pass. Commit: `feat: config page, catalog packaging and release workflow`.
