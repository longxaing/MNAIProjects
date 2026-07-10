using System.Security.Claims;

namespace MnaiWork.Api.Infrastructure;

/// <summary>Resolves a stable per-user identity from the current request principal.</summary>
public interface ICurrentUser
{
    string Id { get; }
    string? Email { get; }
}

public sealed class CurrentUser : ICurrentUser
{
    public CurrentUser(IHttpContextAccessor accessor)
    {
        var principal = accessor.HttpContext?.User;

        Id = FirstClaim(principal,
                "oid",
                "http://schemas.microsoft.com/identity/claims/objectidentifier",
                ClaimTypes.NameIdentifier,
                "sub")
            ?? throw new UnauthorizedAccessException("The request does not carry a user identity.");

        Email = FirstClaim(principal,
            "preferred_username",
            ClaimTypes.Email,
            "email",
            ClaimTypes.Upn);
    }

    public string Id { get; }
    public string? Email { get; }

    private static string? FirstClaim(ClaimsPrincipal? principal, params string[] types)
    {
        if (principal is null)
        {
            return null;
        }
        foreach (var type in types)
        {
            var value = principal.FindFirst(type)?.Value;
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        return null;
    }
}
