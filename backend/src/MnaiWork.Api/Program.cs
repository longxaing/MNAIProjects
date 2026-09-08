using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Agent.Skills;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.BuildExecution;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Data;
using MnaiWork.Api.Deployment;
using MnaiWork.Api.Generation;
using MnaiWork.Api.Infrastructure;
using MnaiWork.Api.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using OpenAI;
using OpenAI.Responses;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "spa";

// ---------------------------------------------------------------------------
// Key Vault: when KeyVault:Uri is configured, merge its secrets into the
// configuration so ApiKeys / connection strings can live in the vault instead
// of appsettings. Secret names map "--" -> ":" (e.g. Cosmos--Key -> Cosmos:Key).
// Auth uses DefaultAzureCredential (az login locally, managed identity in Azure).
// ---------------------------------------------------------------------------
var keyVaultUri = builder.Configuration[$"{KeyVaultOptions.SectionName}:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// The software-factory profile must already exist in Cosmos. We intentionally do not fall back to
// Key Vault or App Configuration for these editable settings.
var cosmosSettings = builder.Configuration.GetSection(CosmosOptions.SectionName).Get<CosmosOptions>()
    ?? new CosmosOptions();
if (string.IsNullOrWhiteSpace(cosmosSettings.Endpoint))
{
    throw new InvalidOperationException(
        "Cosmos:Endpoint must be configured before the Cosmos deployment profile can be loaded.");
}
var bootstrapClientOptions = new CosmosClientOptions
{
    Serializer = new SystemTextJsonCosmosSerializer(JsonDefaults.Options),
    ConnectionMode = ConnectionMode.Direct
};
var bootstrapCosmosClient = string.IsNullOrWhiteSpace(cosmosSettings.Key)
    ? new CosmosClient(cosmosSettings.Endpoint, new DefaultAzureCredential(), bootstrapClientOptions)
    : new CosmosClient(cosmosSettings.Endpoint, cosmosSettings.Key, bootstrapClientOptions);
var bootstrapCosmosContext = new CosmosContext(
    bootstrapCosmosClient, Options.Create(cosmosSettings));
await bootstrapCosmosContext.InitializeAsync();
IDeploymentProfileRepository bootstrapProfileRepository =
    new DeploymentProfileRepository(bootstrapCosmosContext);
var deploymentProfile = DeploymentProfileService.RequireExisting(
    await bootstrapProfileRepository.GetAsync());

var profileConfigurationSource = new DeploymentProfileConfigurationSource(
    deploymentProfile.ToConfiguration());
((IConfigurationBuilder)builder.Configuration).Add(profileConfigurationSource);
var profileConfigurationProvider = profileConfigurationSource.Provider
    ?? throw new InvalidOperationException("Deployment profile configuration provider was not created.");

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------
builder.Services.Configure<AzureOpenAiOptions>(builder.Configuration.GetSection(AzureOpenAiOptions.SectionName));
builder.Services.Configure<CosmosOptions>(builder.Configuration.GetSection(CosmosOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<AzureProvisioningOptions>(
    builder.Configuration.GetSection(AzureProvisioningOptions.SectionName));
builder.Services.Configure<BuildExecutionOptions>(
    builder.Configuration.GetSection(BuildExecutionOptions.SectionName));
builder.Services.AddSingleton(profileConfigurationProvider);
builder.Services.AddSingleton(deploymentProfile);

// ---------------------------------------------------------------------------
// Azure clients
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

builder.Services.AddSingleton(bootstrapCosmosClient);
builder.Services.AddSingleton(bootstrapCosmosContext);
builder.Services.AddSingleton(bootstrapProfileRepository);

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    return string.IsNullOrWhiteSpace(options.ConnectionString)
        ? new BlobServiceClient(new Uri(options.ServiceUri), new DefaultAzureCredential())
        : new BlobServiceClient(options.ConnectionString);
});

builder.Services.AddHttpClient<ArmProjectDeploymentClient>();
builder.Services.AddHttpClient<IE2BSandboxClient, E2BSandboxClient>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<RuntimeAzureProvisioningOptions>();
builder.Services.AddSingleton<AzureProvisioningOperationGate>();
builder.Services.AddSingleton<BuildExecutor>();

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<AzureOpenAiOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.ApiKey))
    {
        throw new InvalidOperationException(
            "AzureOpenAI:Endpoint and AzureOpenAI:ApiKey must be configured.");
    }
    var clientOptions = new ResponsesClientOptions { Endpoint = new Uri(options.Endpoint) };
    return new ResponsesClient(new ApiKeyCredential(options.ApiKey), clientOptions);
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddScoped<IThreadRepository, ThreadRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<IRunRepository, RunRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddSingleton<DeploymentProfileService>();

builder.Services.AddSingleton<IFileStorage, BlobFileStorage>();
builder.Services.AddSingleton<PptxGenerator>();
builder.Services.AddSingleton<DocxGenerator>();

builder.Services.AddSingleton<IAgentTool, GeneratePptxTool>();
builder.Services.AddSingleton<IAgentTool, GenerateDocxTool>();
builder.Services.AddSingleton<IAgentTool, ListMyFilesTool>();
builder.Services.AddSingleton<IAgentTool, ReadMyFileTool>();
builder.Services.AddSingleton<IAgentTool, ReadAttachmentTool>();
builder.Services.AddSingleton<IAgentTool, LoadSkillTool>();
builder.Services.AddSingleton<IAgentTool, CreateProjectWorkspaceTool>();
builder.Services.AddSingleton<IAgentTool, UpdateProjectWorkspaceTool>();
builder.Services.AddSingleton<IAgentTool, ReadProjectWorkspaceTool>();
builder.Services.AddSingleton<IAgentTool, BuildTestProjectTool>();
builder.Services.AddSingleton<IAgentTool, PreviewAzureProjectTool>();
builder.Services.AddSingleton<IAgentTool, DeployAzureProjectTool>();
builder.Services.AddSingleton<IAgentTool, ListAzureProjectResourcesTool>();
builder.Services.AddSingleton<IAgentTool, GetAzureProjectResourceTool>();
builder.Services.AddSingleton<IAgentTool, GetDeploymentProfileTool>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<IAgentSkill, SoftwareFactorySkill>();
builder.Services.AddSingleton<AgentSkillRegistry>();

builder.Services.AddSingleton<IAgentEventBus, AgentEventBus>();
builder.Services.AddSingleton<IAgentRunQueue, AgentRunQueue>();
builder.Services.AddScoped<ContextManager>();
builder.Services.AddScoped<AgentRunner>();
builder.Services.AddHostedService<AgentRunnerHostedService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// ---------------------------------------------------------------------------
// Auth: Azure AD when configured, otherwise a local dev handler.
// ---------------------------------------------------------------------------
var azureAd = builder.Configuration.GetSection("AzureAd");
var azureAdClientId = azureAd["ClientId"];
var azureAdTenantId = azureAd["TenantId"];
var useAzureAdInDevelopment = builder.Configuration.GetValue<bool>(
    "Authentication:UseAzureAdInDevelopment");
var sharedAuthority = azureAdTenantId is "common" or "organizations" or "consumers";
if (!string.IsNullOrWhiteSpace(azureAdClientId)
    && (string.IsNullOrWhiteSpace(azureAdTenantId) || sharedAuthority)
    && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "AzureAd:TenantId must be a concrete tenant id in non-development environments.");
}

var provisioningEnabled = builder.Configuration.GetValue<bool>("AzureProvisioning:Enabled");
var provisioningTenantId = builder.Configuration["AzureProvisioning:TenantId"];
if (provisioningEnabled
    && !string.Equals(azureAdTenantId, provisioningTenantId, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "AzureAd:TenantId must match AzureProvisioning:TenantId when provisioning is enabled.");
}

var useAzureAd = (!builder.Environment.IsDevelopment() || useAzureAdInDevelopment)
    && !string.IsNullOrWhiteSpace(azureAdClientId)
    && !string.IsNullOrWhiteSpace(azureAdTenantId)
    && !sharedAuthority;
if (useAzureAd)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(azureAd);

    builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        // v2.0 access tokens carry the bare client id as `aud`, while v1.0 carry "api://<id>".
        // Accept both so either token version validates.
        var clientId = azureAdClientId;
        var appIdUri = azureAd["Audience"];
        var audiences = new[] { clientId, appIdUri, $"api://{clientId}" }
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .ToArray();
        options.TokenValidationParameters.ValidAudiences = audiences;
    });
}
else
{
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
}
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        "DeploymentProfileAdmin",
        policy => policy.RequireRole("MnaiWork.DeploymentAdmin"));
});

// ---------------------------------------------------------------------------
// MVC / CORS / Swagger
// ---------------------------------------------------------------------------
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();
