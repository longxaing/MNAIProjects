using MnaiWork.Api.Data;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MnaiWork.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly ICurrentUser _me;

    public UsersController(IUserRepository users, ICurrentUser me)
    {
        _users = users;
        _me = me;
    }

    /// <summary>
    /// Returns the current user, creating the record on first sign-in and refreshing
    /// profile/last-seen on subsequent calls. The SPA calls this right after login.
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<User>> Me(CancellationToken ct)
    {
        var user = await _users.RecordSignInAsync(
            _me.Id, _me.TenantId, _me.Email, _me.DisplayName, _me.IsPersonalAccount, ct);
        return Ok(user);
    }
}
