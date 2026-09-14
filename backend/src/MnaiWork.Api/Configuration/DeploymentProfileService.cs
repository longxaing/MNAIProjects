using MnaiWork.Api.Data;
using MnaiWork.Api.Models;

namespace MnaiWork.Api.Configuration;

public sealed class DeploymentProfileService
{
    public const string MissingProfileMessage =
        "Cosmos deploymentProfiles/default is missing. Create the software-factory deployment " +
        "profile in Cosmos Data Explorer before starting the Agent API. No Key Vault or App " +
        "Configuration fallback is used for AzureProvisioning, BuildExecution, or AzureAd:TenantId.";

    private readonly IDeploymentProfileRepository _repository;
    private readonly DeploymentProfileConfigurationProvider _configuration;
    private readonly AzureProvisioningOperationGate _operationGate;
    private DeploymentProfile _current;

    public DeploymentProfileService(
        IDeploymentProfileRepository repository,
        DeploymentProfileConfigurationProvider configuration,
        DeploymentProfile initialProfile,
        AzureProvisioningOperationGate? operationGate = null)
    {
        _repository = repository;
        _configuration = configuration;
        _operationGate = operationGate ?? new AzureProvisioningOperationGate();
        _current = initialProfile;
    }

    public DeploymentProfile Current => Volatile.Read(ref _current);

    public static DeploymentProfile RequireExisting(DeploymentProfile? profile)
        => profile ?? throw new InvalidOperationException(MissingProfileMessage);

    public async Task<DeploymentProfile> ReplaceAsync(
        DeploymentProfile requested,
        string etag,
        string updatedBy,
        CancellationToken ct)
    {
        using var operation = await _operationGate.EnterAsync(ct);
        Validate(requested);
        requested.Id = DeploymentProfile.DefaultId;
        requested.Version = checked(Current.Version + 1);
        requested.UpdatedBy = updatedBy;
        requested.UpdatedAt = DateTimeOffset.UtcNow;
        requested.ETag = null;

        var saved = await _repository.ReplaceAsync(requested, etag, ct);
        Volatile.Write(ref _current, saved);
        _configuration.Update(saved.ToConfiguration());
        return saved;
    }

    public static void Validate(DeploymentProfile profile)
    {
        if (!Guid.TryParse(profile.TenantId, out _)
            || !Guid.TryParse(profile.SubscriptionId, out _)
            || !Guid.TryParse(profile.DeploymentPrincipalId, out _)
            || !Guid.TryParse(profile.AzureAdTenantId, out _))
        {
            throw new ArgumentException(
                "Tenant, subscription, deployment principal, and Azure AD tenant must be GUIDs.");
        }
        if (!string.Equals(profile.TenantId, profile.AzureAdTenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Azure provisioning tenant must match Azure AD tenant.");
        }
        if (string.IsNullOrWhiteSpace(profile.GeneratedResourceGroup)
            || string.IsNullOrWhiteSpace(profile.Location)
            || string.IsNullOrWhiteSpace(profile.AppServicePlanName))
        {
            throw new ArgumentException("Resource group, location, and App Service Plan are required.");
        }
        AzureProvisioningOptions.ValidatePlanSelection(
            profile.SubscriptionId, profile.ExistingAppServicePlanResourceId, profile.AppServicePlanOs);
        if (profile.AzureTimeoutMinutes is < 1 or > 120
            || profile.CommandTimeoutMinutes is < 1 or > 30
            || profile.TotalTimeoutMinutes is < 1 or > 60
            || profile.MaxConcurrentBuilds is < 1 or > 4)
        {
            throw new ArgumentException("Timeout or build concurrency values are outside allowed ranges.");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                profile.PlaywrightVersion, "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
        {
            throw new ArgumentException("PlaywrightVersion must be a semantic version such as 1.62.1.");
        }
    }
}