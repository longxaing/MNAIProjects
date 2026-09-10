using Microsoft.Extensions.Options;

namespace MnaiWork.Api.Configuration;

/// <summary>Azure Key Vault settings. When <see cref="Uri"/> is set, secrets are merged into config.</summary>
public sealed class KeyVaultOptions
{
    public const string SectionName = "KeyVault";

    /// <summary>Vault URI, e.g. https://my-kv.vault.azure.net/ . Empty disables Key Vault.</summary>
    public string Uri { get; set; } = string.Empty;
}

/// <summary>Azure OpenAI (Responses API) connection settings.</summary>
public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>Base endpoint, e.g. https://my-resource.services.ai.azure.com/openai/v1 </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API key. Leave empty to use Entra ID (DefaultAzureCredential).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Deployment / model name, e.g. gpt-5.1</summary>
    public string Deployment { get; set; } = "gpt-5.1";

    /// <summary>Max ReAct iterations (model &lt;-&gt; tool round trips) per run.</summary>
    public int MaxToolIterations { get; set; } = 30;

    /// <summary>Approx history size (in tokens) above which older turns get summarized.</summary>
    public int MaxContextTokens { get; set; } = 100000;

    /// <summary>Approx token budget of the most recent turns kept verbatim when summarizing.</summary>
    public int RecentContextTokens { get; set; } = 60000;

    /// <summary>Always keep at least this many of the most recent turns verbatim.</summary>
    public int MinRecentTurns { get; set; } = 4;
}

/// <summary>Azure Cosmos DB (NoSQL) settings.</summary>
public sealed class CosmosOptions
{
    public const string SectionName = "Cosmos";

    /// <summary>Account endpoint, e.g. https://my-account.documents.azure.com:443/ </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Account key. Leave empty to use Entra ID (DefaultAzureCredential).</summary>
    public string Key { get; set; } = string.Empty;

    public string Database { get; set; } = "MnaiWork";
    public string ThreadsContainer { get; set; } = "threads";
    public string MessagesContainer { get; set; } = "messages";
    public string RunsContainer { get; set; } = "runs";
    public string UsersContainer { get; set; } = "users";
    public string DeploymentProfilesContainer { get; set; } = "deploymentProfiles";
}

/// <summary>Azure Blob Storage settings for generated artifacts.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Blob service URI, e.g. https://myaccount.blob.core.windows.net </summary>
    public string ServiceUri { get; set; } = string.Empty;

    /// <summary>Optional connection string. When set, takes precedence over ServiceUri + Entra ID.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string Container { get; set; } = "artifacts";

    /// <summary>Minutes a generated-file SAS/download link stays valid.</summary>
    public int DownloadLinkTtlMinutes { get; set; } = 120;
}

/// <summary>Fixed single-subscription target for generated demo projects.</summary>
public sealed class AzureProvisioningOptions
{
    public const string SectionName = "AzureProvisioning";

    public bool Enabled { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string GeneratedResourceGroup { get; set; } = "rg-mnaiwork-generated-demo";
    public string Location { get; set; } = "canadacentral";
    public string CosmosLocation { get; set; } = string.Empty;
    public string AppServicePlanName { get; set; } = "asp-mnaiwork-generated-demo";
    public string ExistingAppServicePlanResourceId { get; set; } = string.Empty;
    public string AppServicePlanOs { get; set; } = "Windows";
    public string DeploymentPrincipalId { get; set; } = string.Empty;
    public int TimeoutMinutes { get; set; } = 30;

    public static void ValidatePlanSelection(string subscriptionId, string existingPlanId, string operatingSystem)
    {
        if (operatingSystem != "Windows")
        {
            throw new ArgumentException("Only Windows App Service Plans are supported. Set AppServicePlanOs to Windows in DeploymentProfile.");
        }
        if (string.IsNullOrEmpty(existingPlanId))
        {
            return;
        }
        var parts = existingPlanId.Split('/');
        if (parts.Length != 9 || parts[0] != string.Empty
            || !string.Equals(parts[1], "subscriptions", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[2], subscriptionId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[3], "resourceGroups", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[5], "providers", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[6], "Microsoft.Web", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[7], "serverfarms", StringComparison.OrdinalIgnoreCase)
            || !System.Text.RegularExpressions.Regex.IsMatch(parts[4], @"^[\w.()-]+$")
            || !System.Text.RegularExpressions.Regex.IsMatch(parts[8], @"^[a-zA-Z0-9-]+$"))
        {
            throw new ArgumentException("Existing App Service Plan must be a serverfarms resource ID in the configured subscription.");
        }
    }
}

/// <summary>Reads the latest Azure provisioning options after DeploymentProfile reloads.</summary>
public sealed class RuntimeAzureProvisioningOptions
{
    private readonly Func<AzureProvisioningOptions> _current;

    public RuntimeAzureProvisioningOptions(IOptionsMonitor<AzureProvisioningOptions> monitor)
        : this(() => monitor.CurrentValue)
    {
    }

    private RuntimeAzureProvisioningOptions(Func<AzureProvisioningOptions> current)
        => _current = current;

    public static RuntimeAzureProvisioningOptions Fixed(AzureProvisioningOptions options)
        => new(() => options);

    private AzureProvisioningOptions Current => _current();

    public bool Enabled => Current.Enabled;
    public string TenantId => Current.TenantId;
    public string SubscriptionId => Current.SubscriptionId;
    public string GeneratedResourceGroup => Current.GeneratedResourceGroup;
    public string Location => Current.Location;
    public string CosmosLocation => string.IsNullOrWhiteSpace(Current.CosmosLocation)
        ? Current.Location
        : Current.CosmosLocation.Trim();
    public string AppServicePlanName => Current.AppServicePlanName;
    public string ExistingAppServicePlanResourceId => Current.ExistingAppServicePlanResourceId;
    public string AppServicePlanOs => Current.AppServicePlanOs;
    public string DeploymentPrincipalId => Current.DeploymentPrincipalId;
    public int TimeoutMinutes => Current.TimeoutMinutes;
}

public sealed class AzureProvisioningOperationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

/// <summary>Controls generated project builds executed in E2B sandboxes.</summary>
public sealed class BuildExecutionOptions
{
    public const string SectionName = "BuildExecution";

    public bool Enabled { get; set; }
    public int MaxConcurrentBuilds { get; set; } = 1;
    public int CommandTimeoutMinutes { get; set; } = 15;
    public int TotalTimeoutMinutes { get; set; } = 45;
    public string PlaywrightVersion { get; set; } = "1.62.1";
}

