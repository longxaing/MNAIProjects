using System.Security.Claims;

namespace MnaiWork.Api.Infrastructure;

/// <summary>Microsoft Account (MSA / personal) tenant id — identifies non-organizational accounts.</summary>
public static class AadConstants
{
    public const string MsaTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";
}

/// <summary>Resolves a stable per-user identity from the current request principal.</summary>
public interface ICurrentUser
{
    string Id { get; }
    string? Email { get; }
    string? TenantId { get; }
    string? DisplayName { get; }
    bool IsPersonalAccount { get; }
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

        TenantId = FirstClaim(principal,
            "tid",
            "http://schemas.microsoft.com/identity/claims/tenantid");

        DisplayName = FirstClaim(principal, "name", ClaimTypes.GivenName);

        IsPersonalAccount = string.Equals(TenantId, AadConstants.MsaTenantId, StringComparison.OrdinalIgnoreCase);
    }

    public string Id { get; }
    public string? Email { get; }
    public string? TenantId { get; }
    public string? DisplayName { get; }
    public bool IsPersonalAccount { get; }

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
