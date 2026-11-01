using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.WatchParty.Commands;
using Jellyfin.Plugin.WatchParty.Configuration;
using Jellyfin.Plugin.WatchParty.Discord;
using Jellyfin.Plugin.WatchParty.Voting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WatchParty.Api;

/// <summary>Body of POST /WatchParty/Command.</summary>
public sealed class CommandRequest
{
    /// <summary>pause, resume, skip, stop, pick or cancel. Ignored when <see cref="Vote"/> is set.</summary>
    public string? Action { get; set; }

    public string? User { get; set; }

    public bool Force { get; set; }

    /// <summary>yes, no, an option key, or a 0-based option index.</summary>
    public string? Vote { get; set; }

    public Guid? VoteId { get; set; }
}

public sealed record CommandResponse(string Kind, string Message, Guid? VoteId);

public sealed record BotStatusResponse(string State, string? BotUserName, string? ApplicationId, string? InviteUrl);

/// <summary>Generic webhook for local tools, plus bot status for the settings page.</summary>
[ApiController]
[Route("WatchParty")]
public sealed class CommandController : ControllerBase
{
    private readonly CommandRouter _router;
    private readonly Func<PluginConfiguration> _config;
    private readonly IBotStatus _bot;

    public CommandController(CommandRouter router, Func<PluginConfiguration> config, IBotStatus bot)
    {
        _router = router;
        _config = config;
        _bot = bot;
    }

    [HttpPost("Command")]
    [AllowAnonymous]
    public async Task<IActionResult> Command([FromBody] CommandRequest request, [FromHeader(Name = "X-WatchParty-Secret")] string? secret, CancellationToken ct)
    {
        var expected = _config().WebhookSecret;
        if (string.IsNullOrEmpty(expected))
        {
            return NotFound();
        }

        if (secret is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(expected)))
        {
            return Unauthorized();
        }

        var user = string.IsNullOrWhiteSpace(request.User) ? "webhook" : request.User.Trim();

        if (!string.IsNullOrWhiteSpace(request.Vote))
        {
            var active = _router.ActiveVote;
            if (active is null)
            {
                return Reply(new RouterReply(ReplyKind.Error, CommandRouter.VoteEnded));
            }

            var key = request.Vote.Trim();
            if (int.TryParse(key, out var index))
            {
                if (index < 0 || index >= active.Options.Count)
                {
                    return BadRequest(new CommandResponse("Error", $"Option index must be 0-{active.Options.Count - 1}", active.Id));
                }

                key = active.Options[index].Key;
            }

            return Reply(_router.Cast(request.VoteId ?? active.Id, user, key));
        }

        var action = request.Action?.Trim().ToLowerInvariant();
        if (action == "cancel")
        {
            return Reply(_router.Cancel(request.Force));
        }

        if (action is not ("pause" or "resume" or "skip" or "stop" or "pick"))
        {
            return BadRequest(new CommandResponse("Error", "action must be pause, resume, skip, stop, pick or cancel", null));
        }

        var voteAction = Enum.Parse<VoteAction>(action, ignoreCase: true);
        return Reply(await _router.RequestAsync(voteAction, user, request.Force, null, ct).ConfigureAwait(false));
    }

    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Status()
    {
        var appId = _bot.ApplicationId;
        var invite = appId is null ? null : $"https://discord.com/oauth2/authorize?client_id={appId}&scope=bot%20applications.commands&permissions=0";
        return Ok(new BotStatusResponse(_bot.State.ToString(), _bot.BotUserName, appId, invite));
    }

    private ObjectResult Reply(RouterReply reply)
    {
        var body = new CommandResponse(reply.Kind.ToString(), reply.Message, reply.Vote?.Id ?? _router.ActiveVote?.Id);
        return StatusCode(reply.Kind == ReplyKind.Error ? StatusCodes.Status409Conflict : StatusCodes.Status200OK, body);
    }
}
