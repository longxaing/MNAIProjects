using Microsoft.AspNetCore.Authorization;

namespace MnaiWork.Api.Sharing;

public static class ShareAuthorization
{
    public const string OwnerPolicy = "ShareOwner";

    public static void Configure(AuthorizationOptions options, bool allowManagement)
        => options.AddPolicy(OwnerPolicy, policy => policy.RequireAuthenticatedUser()
            .RequireAssertion(_ => allowManagement));
}