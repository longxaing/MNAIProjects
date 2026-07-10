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
    public int MaxToolIterations { get; set; } = 8;

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
