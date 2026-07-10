using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MnaiWork.Api.Infrastructure;

/// <summary>
/// A development-only authentication handler that authenticates every request as a fixed local
/// user. Enabled only when Azure AD is not configured, so <c>[Authorize]</c> works without tokens.
/// The client may set <c>X-Debug-User</c> to simulate different users.
/// </summary>
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevAuth";

    public DevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers["X-Debug-User"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = "local-dev-user";
        }

        var claims = new[]
        {
            new Claim("oid", userId),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim("preferred_username", $"{userId}@localhost")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
