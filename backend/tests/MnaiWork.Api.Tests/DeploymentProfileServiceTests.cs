using MnaiWork.Api.Configuration;
using MnaiWork.Api.Data;
using MnaiWork.Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MnaiWork.Api.Tests;

public sealed class DeploymentProfileServiceTests
{
    [Fact]
    public void AzureOpenAiOptions_DefaultToolIterationBudgetSupportsSoftwareFactory()
    {
        var options = new AzureOpenAiOptions();

        Assert.Equal(30, options.MaxToolIterations);
    }

    [Fact]
    public void BuildExecutionOptions_DefaultTimeoutsSupportColdSandboxBuilds()
    {
        var options = new BuildExecutionOptions();

        Assert.Equal(15, options.CommandTimeoutMinutes);
        Assert.Equal(45, options.TotalTimeoutMinutes);
    }

    [Fact]
    public async Task ReplaceAsync_UpdatesCosmosProfileAndReloadsRuntimeOptions()
    {
        var initial = ValidProfile();
        initial.ETag = "etag-1";
        var source = new DeploymentProfileConfigurationSource(initial.ToConfiguration());
        var configuration = new ConfigurationBuilder().Add(source).Build();
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<AzureProvisioningOptions>(
            configuration.GetSection(AzureProvisioningOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        var runtime = new RuntimeAzureProvisioningOptions(
            provider.GetRequiredService<IOptionsMonitor<AzureProvisioningOptions>>());
        var repository = new FakeRepository(initial);
        var service = new DeploymentProfileService(
            repository,
            source.Provider!,
            initial);
        var updated = ValidProfile();
        updated.GeneratedResourceGroup = "rg-updated";
        updated.CosmosLocation = "westus2";
        updated.AzureTimeoutMinutes = 45;
        updated.AppServicePlanOs = "Windows";
        updated.ExistingAppServicePlanResourceId = $"/subscriptions/{updated.SubscriptionId}/resourceGroups/rg-existing/providers/Microsoft.Web/serverfarms/existing-plan";

        var saved = await service.ReplaceAsync(
            updated, "etag-1", "user-1", CancellationToken.None);

        Assert.Equal("rg-updated", runtime.GeneratedResourceGroup);
        Assert.Equal("westus2", runtime.CosmosLocation);
        Assert.Equal(updated.Location, runtime.Location);
        Assert.Equal(45, runtime.TimeoutMinutes);
        Assert.Equal("Windows", runtime.AppServicePlanOs);
        Assert.Equal(updated.ExistingAppServicePlanResourceId, runtime.ExistingAppServicePlanResourceId);
        Assert.Equal("etag-2", saved.ETag);
        Assert.Equal(2, saved.Version);
        Assert.Equal("user-1", saved.UpdatedBy);
    }

    [Fact]
    public void DefaultPlanSelection_CreatesWindowsPlanInCanadaCentral()
    {
        var options = new AzureProvisioningOptions();
        var profile = new DeploymentProfile();
        Assert.Equal("Windows", options.AppServicePlanOs);
        Assert.Equal("Windows", profile.AppServicePlanOs);
        Assert.Equal("canadacentral", options.Location);
        Assert.Equal("canadacentral", profile.Location);
        Assert.Empty(options.ExistingAppServicePlanResourceId);
        Assert.Empty(profile.ExistingAppServicePlanResourceId);
        Assert.Empty(profile.CosmosLocation);
        Assert.Equal(options.Location, RuntimeAzureProvisioningOptions.Fixed(options).CosmosLocation);
        DeploymentProfileService.Validate(ValidProfile());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void CosmosLocation_FallsBackToLocationWhenUnset(string? cosmosLocation)
    {
        var options = new AzureProvisioningOptions { Location = "eastus2", CosmosLocation = cosmosLocation! };
        Assert.Equal("eastus2", RuntimeAzureProvisioningOptions.Fixed(options).CosmosLocation);
    }

    [Fact]
    public void Validate_RejectsInvalidDeploymentBoundary()
    {
        var profile = ValidProfile();
        profile.SubscriptionId = "not-a-guid";

        Assert.Throws<ArgumentException>(() => DeploymentProfileService.Validate(profile));
    }

    [Fact]
    public void RequireExisting_ExplainsHowToCreateMissingCosmosProfile()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => DeploymentProfileService.RequireExisting(null));

        Assert.Contains("deploymentProfiles/default", error.Message, StringComparison.Ordinal);
        Assert.Contains("Cosmos Data Explorer", error.Message, StringComparison.Ordinal);
        Assert.Contains("No Key Vault", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Linux", "")]
    [InlineData("Other", "")]
    [InlineData("Windows", "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-existing/providers/Microsoft.Web/serverfarms/plan")]
    [InlineData("Windows", "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-existing/providers/Microsoft.Web/sites/app")]
    public void Validate_RejectsInvalidExistingPlan(string operatingSystem, string planId)
    {
        var profile = ValidProfile();
        profile.AppServicePlanOs = operatingSystem;
        profile.ExistingAppServicePlanResourceId = planId;

        Assert.Throws<ArgumentException>(() => DeploymentProfileService.Validate(profile));
    }

    private static DeploymentProfile ValidProfile() => new()
    {
        AzureProvisioningEnabled = true,
        TenantId = "11111111-1111-1111-1111-111111111111",
        SubscriptionId = "22222222-2222-2222-2222-222222222222",
        GeneratedResourceGroup = "rg-generated",
        Location = "eastus2",
        AppServicePlanName = "shared-plan",
        DeploymentPrincipalId = "33333333-3333-3333-3333-333333333333",
        AzureTimeoutMinutes = 30,
        BuildExecutionEnabled = true,
        MaxConcurrentBuilds = 1,
        CommandTimeoutMinutes = 10,
        TotalTimeoutMinutes = 30,
        PlaywrightVersion = "1.62.1",
        AzureAdTenantId = "11111111-1111-1111-1111-111111111111",
        Version = 1
    };

    private sealed class FakeRepository : IDeploymentProfileRepository
    {
        private DeploymentProfile _profile;

        public FakeRepository(DeploymentProfile profile) => _profile = profile;

        public Task<DeploymentProfile?> GetAsync(CancellationToken ct = default)
            => Task.FromResult<DeploymentProfile?>(_profile);

        public Task<DeploymentProfile> ReplaceAsync(
            DeploymentProfile profile,
            string etag,
            CancellationToken ct = default)
        {
            Assert.Equal("etag-1", etag);
            profile.ETag = "etag-2";
            _profile = profile;
            return Task.FromResult(profile);
        }
    }
}