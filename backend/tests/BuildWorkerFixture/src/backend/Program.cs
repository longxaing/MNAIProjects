using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IDependencyCheck, LocalDependencyCheck>();
    builder.Services.AddSingleton<INoteRepository, InMemoryNoteRepository>();
    builder.Services.AddSingleton<IAppFileStore, InMemoryAppFileStore>();
    builder.Services.AddSingleton<IAppSecrets, LocalAppSecrets>();
}
else
{
    builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
    builder.Services.AddSingleton(sp => new BlobServiceClient(
        new Uri(builder.Configuration["Storage:ServiceUri"]!),
        sp.GetRequiredService<TokenCredential>()));
    builder.Services.AddSingleton(sp => new CosmosClient(
        builder.Configuration["Cosmos:Endpoint"]!,
        sp.GetRequiredService<TokenCredential>()));
    builder.Services.AddSingleton(sp => new SecretClient(
        new Uri(builder.Configuration["KeyVault:Uri"]!),
        sp.GetRequiredService<TokenCredential>()));
    builder.Services.AddSingleton<IDependencyCheck, AzureDependencyCheck>();
    builder.Services.AddSingleton<INoteRepository, CosmosNoteRepository>();
    builder.Services.AddSingleton<IAppFileStore, BlobAppFileStore>();
    builder.Services.AddSingleton<IAppSecrets, KeyVaultAppSecrets>();
}

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (builder.Configuration["Frontend:Origin"] is { Length: > 0 } origin)
    {
        policy.WithOrigins(origin.TrimEnd('/')).AllowAnyHeader().AllowAnyMethod();
    }
}));

var app = builder.Build();

app.UseCors();
app.MapGet("/health", (IConfiguration configuration) => Results.Ok(new
{
    status = "healthy",
    deploymentFingerprint = configuration["Deployment:Fingerprint"] ?? "local",
    buildId = ReadBuildId()
})).AllowAnonymous();
app.MapGet("/ready", async (
    HttpRequest request,
    IConfiguration configuration,
    IServiceProvider services,
    CancellationToken ct) =>
{
    var expectedFingerprint = configuration["Deployment:Fingerprint"] ?? "local";
    if (!string.Equals(
            request.Query["fingerprint"], expectedFingerprint, StringComparison.Ordinal))
    {
        return Results.NotFound();
    }
    await services.GetRequiredService<IDependencyCheck>().CheckAsync(ct);
    return Results.Ok(new
    {
        status = "ready",
        deploymentFingerprint = expectedFingerprint,
        buildId = ReadBuildId()
    });
}).AllowAnonymous();
app.MapGet("/api/sum/{left:int}/{right:int}", (int left, int right) => Calculator.Add(left, right));

if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/fixture-notes", (INoteRepository notes, CancellationToken ct) => notes.ListAsync(ct));
    app.MapPost("/api/fixture-notes", async ([Microsoft.AspNetCore.Mvc.FromBody] string note, INoteRepository notes, CancellationToken ct) =>
    {
        await notes.AddAsync(note, ct);
        return Results.Ok(note);
    });
}

app.Run();

static string ReadBuildId()
{
    var path = Path.Combine(AppContext.BaseDirectory, "deployment-manifest.json");
    if (!File.Exists(path))
    {
        return "local";
    }
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    return document.RootElement.GetProperty("buildId").GetString() ?? "unknown";
}

public static class Calculator
{
    public static int Add(int left, int right) => left + right;
}

public partial class Program;