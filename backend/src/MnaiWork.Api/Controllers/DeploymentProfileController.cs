using System.Net;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;

namespace MnaiWork.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/deployment-profile")]
public sealed class DeploymentProfileController : ControllerBase
{
    private readonly DeploymentProfileService _profiles;
    private readonly ICurrentUser _me;

    public DeploymentProfileController(
        DeploymentProfileService profiles,
        ICurrentUser me)
    {
        _profiles = profiles;
        _me = me;
    }

    [HttpGet]
    public ActionResult<DeploymentProfile> Get()
    {
        var profile = _profiles.Current;
        Response.Headers.ETag = profile.ETag;
        return Ok(profile);
    }

    [HttpPut]
    [Authorize(Policy = "DeploymentProfileAdmin")]
    public async Task<ActionResult<object>> Put(
        [FromBody] DeploymentProfile requested,
        CancellationToken ct)
    {
        var etag = Request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(etag))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                "If-Match with the current profile ETag is required.");
        }

        var previousTenant = _profiles.Current.AzureAdTenantId;
        try
        {
            var saved = await _profiles.ReplaceAsync(requested, etag, _me.Id, ct);
            Response.Headers.ETag = saved.ETag;
            return Ok(new
            {
                profile = saved,
                restartRequired = !string.Equals(
                    previousTenant, saved.AzureAdTenantId, StringComparison.OrdinalIgnoreCase)
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return Conflict("The profile changed. Reload it and retry with the latest ETag.");
        }
    }
}