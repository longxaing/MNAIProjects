using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Storage.Blobs;
using MnaiWork.Api.Agent;
using MnaiWork.Api.Agent.Tools;
using MnaiWork.Api.Configuration;
using MnaiWork.Api.Data;
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

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------
builder.Services.Configure<AzureOpenAiOptions>(builder.Configuration.GetSection(AzureOpenAiOptions.SectionName));
builder.Services.Configure<CosmosOptions>(builder.Configuration.GetSection(CosmosOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

// ---------------------------------------------------------------------------
// Azure clients
// ---------------------------------------------------------------------------
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
    var clientOptions = new CosmosClientOptions
    {
        Serializer = new SystemTextJsonCosmosSerializer(JsonDefaults.Options),
        ConnectionMode = ConnectionMode.Direct
    };
    return string.IsNullOrWhiteSpace(options.Key)
        ? new CosmosClient(options.Endpoint, new DefaultAzureCredential(), clientOptions)
        : new CosmosClient(options.Endpoint, options.Key, clientOptions);
});
builder.Services.AddSingleton<CosmosContext>();

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    return string.IsNullOrWhiteSpace(options.ConnectionString)
        ? new BlobServiceClient(new Uri(options.ServiceUri), new DefaultAzureCredential())
        : new BlobServiceClient(options.ConnectionString);
});

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

builder.Services.AddSingleton<IFileStorage, BlobFileStorage>();
builder.Services.AddSingleton<PptxGenerator>();
builder.Services.AddSingleton<DocxGenerator>();

builder.Services.AddSingleton<IAgentTool, GeneratePptxTool>();
builder.Services.AddSingleton<IAgentTool, GenerateDocxTool>();
builder.Services.AddSingleton<IAgentTool, ListMyFilesTool>();
builder.Services.AddSingleton<IAgentTool, ReadMyFileTool>();
builder.Services.AddSingleton<IAgentTool, ReadAttachmentTool>();
builder.Services.AddSingleton<ToolRegistry>();

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
var useAzureAd = !string.IsNullOrWhiteSpace(azureAd["ClientId"]);
if (useAzureAd)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(azureAd);

    // Multi-tenant + personal accounts (authority "common"): tokens come from many issuers
    // (each tenant + the MSA tenant), so accept any Microsoft issuer instead of a single one.
    // Security still holds via signature + audience (aud must match this API).
    builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters.ValidateIssuer = false;

        // v2.0 access tokens carry the bare client id as `aud`, while v1.0 carry "api://<id>".
        // Accept both so either token version validates.
        var clientId = azureAd["ClientId"];
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
builder.Services.AddAuthorization();

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

// Ensure Cosmos database/containers exist (best-effort at startup).
var cosmosConfigured = !string.IsNullOrWhiteSpace(
    builder.Configuration.GetSection(CosmosOptions.SectionName)["Endpoint"]);
if (cosmosConfigured)
{
    try
    {
        await app.Services.GetRequiredService<CosmosContext>().InitializeAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Cosmos initialization failed. Check the Cosmos configuration.");
    }
}
else
{
    app.Logger.LogWarning("Cosmos is not configured (Cosmos:Endpoint empty). API calls will fail until configured.");
}

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
