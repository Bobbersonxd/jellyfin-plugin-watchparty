# Watch Party Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Jellyfin plugin that lets a Discord watch party (and other webhook senders) control one host Jellyfin session through timed votes.

**Architecture:** Two pure, unit-tested parts, `VoteEngine` and `Ballot`, sit behind a `CommandRouter`. The router decides whether to start a vote or run an action now, and it applies vote results through `IHostController`, a wrapper over `ISessionManager`. Two ASP.NET controllers feed the router: a generic secret-header webhook, and a Discord Interactions endpoint that checks Ed25519 signatures. `DiscordClient` subscribes to vote outcomes and edits Discord messages.

**Tech Stack:** C# / net10.0, Jellyfin 12.0 server DLLs from `/usr/lib/jellyfin`, ASP.NET Core (FrameworkReference), BouncyCastle.Cryptography, xUnit, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-11-01-watch-party-design.md`

## Global Constraints
- Target `net10.0`. Reference `MediaBrowser.Common.dll`, `MediaBrowser.Controller.dll`, `MediaBrowser.Model.dll`, `Jellyfin.Data.dll` and `Jellyfin.Database.Implementations.dll` from `/usr/lib/jellyfin` via `<Reference HintPath>` with `<Private>false</Private>` in the plugin. Test projects use `<Private>true</Private>` so the tests can load them.
- Root namespace and assembly: `Jellyfin.Plugin.WatchParty`. Plugin GUID: `6f1c8a52-3d0e-4b7a-9c41-2e5d8f7a1b90`. Plugin name: `Watch Party`.
- All routes live under `/WatchParty/`: `POST /WatchParty/Discord` and `POST /WatchParty/Command`. Both are `[AllowAnonymous]`, because they authenticate by signature or secret.
- Defaults: `VoteDurationSeconds = 60`, `MinVoters = 1`. The ballot holds at most `5` nominees. Autocomplete returns at most `25` results.
- Yes/no passes when `yes > no && yes + no >= MinVoters`. The vote starter is auto-counted as `yes`.
- Discord's reply limit is 3 s: never await a Discord REST call before returning an interaction response.
- Exact user-facing strings: `"Host screen offline"`, `"A vote is already in progress"`. The generic webhook is turned off (404) when `WebhookSecret` is empty.
- Shipped DLLs: `Jellyfin.Plugin.WatchParty.dll` and `BouncyCastle.Cryptography.dll` only.

## Review Focus
- **Host leaves mid-vote:** the vote passes, but the host session is gone by the time the timer fires. Expect the outcome message "passed but could not be applied: Host screen offline", with no exception. Covered by `Router_PassedVote_HostGoneAtResolve_ReportsNotApplied` in Task 3.
- **Clicks after the vote ends:** a button click on an old vote message, or a `vote` webhook for a vote ID that has already resolved. Expect an ephemeral "This vote has ended" and no change to the next vote. Covered by `Cast_StaleVoteId_ReturnsNoActiveVote` in Task 2.
- **Concurrent clicks:** many users click at the same moment, on different request threads. Every vote must be counted, with no torn dictionary. The engine locks a single object around all of its state. Covered by `Cast_ParallelUsers_AllCounted` in Task 2.
- **Nominated item deleted before the pick resolves:** `PlayAsync` returns false. Expect "could not be applied", and the ballot is not cleared. Covered by `Router_PickWinnerUnplayable_KeepsBallot` in Task 3.
- **Replayed or tampered Discord request:** a body changed after signing, or a malformed hex signature or key. Expect 401, never a 500. Covered by `Verify_MalformedHex_ReturnsFalse` in Task 2b.

---

## File Structure
```
Jellyfin.Plugin.WatchParty.sln
build.yaml
src/Jellyfin.Plugin.WatchParty/
  Jellyfin.Plugin.WatchParty.csproj
  Plugin.cs                         # BasePlugin<PluginConfiguration>, IHasWebPages
  PluginServiceRegistrator.cs       # IPluginServiceRegistrator: DI wiring
  Configuration/PluginConfiguration.cs
  Configuration/configPage.html     # embedded resource
  Voting/VoteTypes.cs               # VoteAction, VoteOption, Vote, VoteResult, VoteSettings, CastStatus
  Voting/VoteEngine.cs
  Voting/Ballot.cs                  # Nominee, AddStatus, Ballot
  Discord/SignatureVerifier.cs
  Discord/InteractionModels.cs      # minimal JSON DTOs for incoming interactions
  Discord/InteractionResponses.cs   # builders for outgoing response JSON + vote message components
  Discord/InteractionsController.cs
  Discord/DiscordClient.cs
  Discord/CommandDefinitions.cs     # slash command JSON for registration
  Playback/IHostController.cs       # IHostController, HostStatus
  Playback/HostController.cs
  Playback/ILibrarySearch.cs
  Playback/LibrarySearch.cs
  Commands/CommandRouter.cs         # RouterReply, ReplyKind, VoteOutcome
  Api/CommandController.cs          # generic webhook + DTO
tests/Jellyfin.Plugin.WatchParty.Tests/
  Jellyfin.Plugin.WatchParty.Tests.csproj
  VoteEngineTests.cs  BallotTests.cs  SignatureVerifierTests.cs
  CommandRouterTests.cs  InteractionResponsesTests.cs  CommandControllerTests.cs
  Fakes/FakeHost.cs
README.md
```

---

### Task 1: Scaffold, plugin shell, config, deploy check

**Files:**
- Create: the sln, both csproj files, `build.yaml`, `Plugin.cs`, `PluginServiceRegistrator.cs` (empty body for now), `Configuration/PluginConfiguration.cs`, a placeholder `Configuration/configPage.html` (the title "Watch Party" and nothing else), `.gitignore` (bin/obj).

**Interfaces:**
- Produces: `PluginConfiguration : BasePluginConfiguration`, with string properties `DiscordPublicKey`, `DiscordApplicationId`, `DiscordBotToken`, `HostRoleId`, `ChannelWebhookUrl`, `HostDeviceName`, `WebhookSecret` (all default `""`) and ints `VoteDurationSeconds = 60` and `MinVoters = 1`. `Plugin.Instance` (static) and `Plugin.Instance.Configuration`.
- `Plugin(IApplicationPaths, IXmlSerializer)`, using the ctor verified in the 12.0 DLL. `GetPages()` returns one `PluginPageInfo { Name = "WatchParty", EmbeddedResourcePath = "Jellyfin.Plugin.WatchParty.Configuration.configPage.html" }`.

- [ ] **Step 1:** Create the projects. The plugin csproj has `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, the five Jellyfin DLL references with `Private=false`, `PackageReference BouncyCastle.Cryptography` (latest 2.x), `<EmbeddedResource Include="Configuration\configPage.html" />` and `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`. The test csproj is xUnit, references the plugin project and `Microsoft.Extensions.TimeProvider.Testing`, and references the same Jellyfin DLLs with `Private=true`.
- [ ] **Step 2:** Run `dotnet build -c Release`. Expected: 0 errors, and `bin/Release/net10.0/` contains `Jellyfin.Plugin.WatchParty.dll` and `BouncyCastle.Cryptography.dll`.
- [ ] **Step 3:** Deploy. Ask Bob to run `! sudo mkdir -p /var/lib/jellyfin/plugins/WatchParty_1.0.0 && sudo cp src/Jellyfin.Plugin.WatchParty/bin/Release/net10.0/{Jellyfin.Plugin.WatchParty,BouncyCastle.Cryptography}.dll /var/lib/jellyfin/plugins/WatchParty_1.0.0/ && sudo systemctl restart jellyfin`. Check first that the plugins dir path is right with `systemctl cat jellyfin` / `/etc/conf.d/jellyfin`. Then check `journalctl -u jellyfin -n 100 | grep -i "watch party"`. Expected: a "Loaded plugin: Watch Party 1.0.0.0" line and no load exception.
- [ ] **Step 4:** Commit: `chore: scaffold Watch Party plugin`.

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

### Task 2b: Discord signature verifier

**Files:** `Discord/SignatureVerifier.cs`; test `SignatureVerifierTests.cs`.

**Produces:** `public static class SignatureVerifier { public static bool Verify(string publicKeyHex, string signatureHex, string timestamp, ReadOnlySpan<byte> body); }`. It checks Ed25519 over `UTF8(timestamp) + body` with BouncyCastle `Ed25519Signer`. Any bad input returns false and never throws.

- [ ] **Step 1: Tests.** Generate a key pair in the test with BouncyCastle (`Ed25519KeyPairGenerator`) and sign `"1700000000" + "{\"type\":1}"`. Then: `Verify_ValidSignature_True`; `Verify_TamperedBody_False`; `Verify_WrongTimestamp_False`; `Verify_MalformedHex_ReturnsFalse` (signature `"zz"`, key `""`).
- [ ] **Step 2:** Run the tests. Expected: FAIL. Implement, run again. Expected: PASS.
- [ ] **Step 3:** Commit: `feat: discord ed25519 signature verification`.

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
- `SendAsync` maps Pause→`Pause`, Resume→`Unpause`, Skip→`NextTrack`, Stop→`Stop`, and calls `SendPlaystateCommand(null, session.Id, new PlaystateRequest{Command=…}, ct)`. A null controlling session skips the control check in 12.0.
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
- [ ] **Step 2:** Run the tests. Expected: FAIL. Implement the router, `HostController` and `LibrarySearch`, then run `dotnet test`. Expected: all pass. HostController and LibrarySearch are checked against the live server in Task 4.
- [ ] **Step 3:** Register singletons in `PluginServiceRegistrator`: `TimeProvider.System`; `VoteEngine`, whose settings delegate reads `Plugin.Instance.Configuration`; `Ballot`; `IHostController`; `ILibrarySearch`; `CommandRouter`. Run `dotnet build`. Expected: 0 errors.
- [ ] **Step 4:** Commit: `feat: host control and command router`.

### Task 4: Generic webhook controller + live end-to-end

**Files:** `Api/CommandController.cs`; test `CommandControllerTests.cs`.

**Produces:**
- `POST /WatchParty/Command`, body `CommandRequest { string Action; string User; bool Force; string? Vote; Guid? VoteId }`.
- The header `X-WatchParty-Secret` is compared with `CryptographicOperations.FixedTimeEquals`.
- An empty config secret gives 404, and a wrong secret gives 401.
- `Action` is one of `pause|resume|skip|stop|pick|cancel`, case-insensitive; anything else gives 400.
- If `Vote` is set, the request calls `router.Cast(VoteId ?? Active.Id, User, key)`. Here `key` is `Vote` as given (`yes`/`no`/an item key), or, when `Vote` parses as an int, the 0-based index into `Active.Options` (out of range gives 400). This matches the spec's `yes|no|<index>`.
- The response is JSON `{kind, message, voteId?}`, with 200 on success, or 409 when the router returns Error.
- To keep the controller testable, the secret comes from an injected `Func<PluginConfiguration>`.

- [ ] **Step 1: Tests**, calling the controller directly with a `DefaultHttpContext`: `EmptySecret_404`; `WrongSecret_401`; `UnknownAction_400`; `ValidPause_StartsVote_200`; `VoteBody_CastsOnActive`; `VoteIndex_MapsToOptionKey`; `VoteIndexOutOfRange_400`.
- [ ] **Step 2:** Run the tests. Expected: FAIL. Implement, run again. Expected: PASS.
- [ ] **Step 3: Live check.** Deploy as in Task 1, Step 3. In the dashboard, set `WebhookSecret=test`, `HostDeviceName=<a browser client's device name>` and `VoteDurationSeconds=15`. Start a movie in that browser, then run:
  `curl -s -XPOST localhost:8096/WatchParty/Command -H 'X-WatchParty-Secret: test' -H 'Content-Type: application/json' -d '{"action":"pause","user":"a"}'`
  Expected: `kind` is VoteStarted, and about 15 s later the browser pauses. Repeat with `"force":true` and the action `resume`. Expected: it resumes right away. Configuration is set manually through `/Plugins/{id}/Configuration` until Task 6 adds the config page.
- [ ] **Step 4:** Commit: `feat: generic command webhook`.

### Task 5: Discord interactions endpoint and client

**Files:** `Discord/InteractionModels.cs`, `Discord/InteractionResponses.cs`, `Discord/InteractionsController.cs`, `Discord/DiscordClient.cs`, `Discord/CommandDefinitions.cs`; test `InteractionResponsesTests.cs`.

**Interfaces:**
- `InteractionResponses` (pure static, returns `JsonObject`):
  - `Pong()` gives `{type:1}`.
  - `Message(string text, bool ephemeral)` gives `{type:4, data:{content, flags: ephemeral?64:0}}`.
  - `VoteMessage(Vote v)` gives a type-4 message. Its content is `"**Vote: <action>** — ends <t:{unix}:R>\n<tally lines>"`, and it has one action row of buttons with `custom_id = "wp:{voteId:N}:{optionKey}"` and the label `"{Label} ({count})"`.
  - `UpdateVote(Vote v)` is the same data with type 7 (UPDATE_MESSAGE), for button clicks.
  - `Autocomplete(IReadOnlyList<Nominee>)` gives `{type:8, data:{choices:[{name (≤100 chars), value: itemId N}]}}`.
- `InteractionsController`:
  - `POST /WatchParty/Discord` calls `Request.EnableBuffering()`, reads the raw bytes and verifies the signature with the headers `X-Signature-Ed25519` and `X-Signature-Timestamp`. A failure gives 401.
  - Interaction type 1 → Pong. Type 2 → a command by `data.name`. Type 3 → parse the `wp:` custom_id, call `router.Cast`, and reply `UpdateVote`, or an ephemeral "This vote has ended" for `NoActiveVote`. Type 4 → autocomplete through `ILibrarySearch.Search(value, 25)`.
  - The user ID is `member.user.id`. `privileged` is true when `member.roles` contains `HostRoleId` and the command has `force:true` (for `/cancelvote`, holding the role is enough).
  - The `/nominate` option value is the item ID string. Look up the title with `ILibraryManager.GetItemById`.
  - A `VoteStarted` reply gives `VoteMessage(vote)`, built with the interaction `token` passed as `discordToken`. Error gives an ephemeral message, and Done/Info a public one.
- `DiscordClient(IHttpClientFactory, ILogger)` subscribes to `router.Completed`:
  - When the vote has a `DiscordToken`, it sends `PATCH https://discord.com/api/v10/webhooks/{appId}/{token}/messages/@original`. The content is the final tally plus the outcome message, with `components: []`.
  - When `ChannelWebhookUrl` is set, it POSTs `{content}`.
  - `RegisterCommandsAsync()` sends `PUT /applications/{appId}/commands` with `Authorization: Bot {DiscordBotToken}` and the body `CommandDefinitions.All`: pause, resume, skip and stop each take an optional boolean `force`; nominate takes a required string `title` with `autocomplete:true`; then ballot, pick, cancelvote and nowplaying.
  - On a non-2xx response it logs and returns false.
- Add a `POST /WatchParty/RegisterCommands` endpoint for the config page button, with `[Authorize(Policy = "RequiresElevation")]` (admin only).

- [ ] **Step 1: Tests.** `Pong_IsType1`; `Ephemeral_SetsFlag64`; `VoteMessage_HasButtonPerOption_WithCustomIds`; `VoteMessage_LabelsShowCounts`; `Autocomplete_TruncatesNameTo100_LimitsTo25`; `ParseCustomId_RoundTrips` (expose `static bool TryParseCustomId(string, out Guid, out string)`).
- [ ] **Step 2:** Run the tests. Expected: FAIL. Implement everything in this task, then run `dotnet test`. Expected: all pass.
- [ ] **Step 3: Live check.** Bob creates a Discord app and puts the public key, app ID and bot token in the config. He starts a tunnel with `cloudflared tunnel --url http://localhost:8096` and sets the Interactions Endpoint URL to `<tunnel>/WatchParty/Discord`. Expected: the portal saves it, which means PING passed. Click Register Commands, then in Discord: `/nominate` shows library autocomplete, `/pick` shows buttons whose counts go up on click, and when the timer ends the message edits to the result and the winner plays.
- [ ] **Step 4:** Commit: `feat: discord interactions endpoint and client`.

### Task 6: Config page and README

**Files:** `Configuration/configPage.html`, `README.md`.

- [ ] **Step 1:** Build the config page on the standard Jellyfin plugin page pattern: `data-role="page"`, `ApiClient.getPluginConfiguration(pluginId)` / `updatePluginConfiguration`.
  - Inputs for every `PluginConfiguration` field. `DiscordBotToken` and `WebhookSecret` use `type=password`.
  - `HostDeviceName` is a `<select>` filled from `ApiClient.getSessions()`, using the device names that support remote control, with a free-text fallback.
  - A Register Commands button POSTs to `/WatchParty/RegisterCommands` and shows a toast with the result.
- [ ] **Step 2: README.** Cover:
  - What the plugin does.
  - Building and installing (the sudo copy command from Task 1).
  - Creating a Discord app (bot scope `applications.commands`) and invite URL.
  - A public URL through a Cloudflare Tunnel, and setting the Interactions URL.
  - Choosing the host device.
  - The command list.
  - A generic webhook curl example.
  - Voting rules.
- [ ] **Step 3:** Deploy, open Dashboard → Plugins → Watch Party, then save and reload. Expected: the values persist and the host dropdown lists the active sessions.
- [ ] **Step 4:** Run `dotnet test`. Expected: all pass. Commit: `feat: config page and README`.
