using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Xunit;

public sealed class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_ReturnsSuccess() =>
        Assert.True((await _client.GetAsync("/health")).IsSuccessStatusCode);

    [Fact]
    public async Task CosmosClient_ResolvesWithRuntimeDependencies()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production").UseSetting(
                "Cosmos:Endpoint", "https://cosmos.example.test"));

        var cosmos = factory.Services.GetRequiredService<CosmosClient>();

        Assert.Equal(new Uri("https://cosmos.example.test"), cosmos.Endpoint);
    }

    [Fact]
    public async Task Development_ReadinessWorksWithoutAzureClientsOrConfiguration()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        Assert.Null(factory.Services.GetService<TokenCredential>());
        Assert.Null(factory.Services.GetService<CosmosClient>());
        Assert.Null(factory.Services.GetService<BlobServiceClient>());
        Assert.Null(factory.Services.GetService<SecretClient>());
        Assert.IsType<LocalDependencyCheck>(factory.Services.GetRequiredService<IDependencyCheck>());
        Assert.True((await client.GetAsync("/ready?fingerprint=local")).IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/ready?fingerprint=wrong")).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopment_DoesNotFallBackToLocalDependencies(string environment)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment)
                .UseSetting("Cosmos:Endpoint", "https://cosmos.example.test")
                .UseSetting("Storage:ServiceUri", "https://storage.example.test")
                .UseSetting("KeyVault:Uri", "https://vault.example.test"));
        Assert.IsType<AzureDependencyCheck>(factory.Services.GetRequiredService<IDependencyCheck>());
        Assert.IsType<CosmosNoteRepository>(factory.Services.GetRequiredService<INoteRepository>());
        Assert.IsType<BlobAppFileStore>(factory.Services.GetRequiredService<IAppFileStore>());
        Assert.IsType<KeyVaultAppSecrets>(factory.Services.GetRequiredService<IAppSecrets>());
        Assert.NotNull(factory.Services.GetRequiredService<TokenCredential>());
    }

    [Fact]
    public async Task Development_UsesLocalBusinessDependenciesWithoutAzure()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development").UseSetting("DevelopmentSecrets:fixture", "non-sensitive-test-value"));
        using var client = factory.CreateClient();
        var notes = factory.Services.GetRequiredService<INoteRepository>();
        Assert.IsType<InMemoryNoteRepository>(notes);
        using var response = await client.PostAsync("/api/fixture-notes",
            System.Net.Http.Json.JsonContent.Create("test-note"));
        response.EnsureSuccessStatusCode();
        Assert.Contains("test-note", await client.GetStringAsync("/api/fixture-notes"));
        Assert.Contains("test-note", await notes.ListAsync(CancellationToken.None));
        var files = factory.Services.GetRequiredService<IAppFileStore>();
        Assert.IsType<InMemoryAppFileStore>(files);
        await files.WriteAsync("test.bin", new byte[] { 1, 2 }, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2 }, await files.ReadAsync("test.bin", CancellationToken.None));
        var secrets = factory.Services.GetRequiredService<IAppSecrets>();
        Assert.IsType<LocalAppSecrets>(secrets);
        Assert.Equal("non-sensitive-test-value", await secrets.ReadAsync("fixture", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => secrets.ReadAsync("missing", CancellationToken.None));
    }

    [Fact]
    public void NoteDocument_PreservesCosmosJsonAndPartitionKeyContract()
    {
        var note = new NoteDocument { Text = "test" };
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(note);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(note.Id, document.RootElement.GetProperty("id").GetString());
        Assert.Equal(note.PartitionKey, document.RootElement.GetProperty("partitionKey").GetString());
        Assert.Equal("fixtureNote", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(note.Text, Newtonsoft.Json.JsonConvert.DeserializeObject<NoteDocument>(json)!.Text);
    }

    [Fact]
    public async Task Production_MissingAzureConfigurationDoesNotResolveLocalReadiness()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production")
                .UseSetting("Storage:ServiceUri", ""));
        using var client = factory.CreateClient();
        Assert.Throws<UriFormatException>(() => factory.Services.GetRequiredService<IDependencyCheck>());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/ready?fingerprint=wrong")).StatusCode);
    }

    [Fact]
    public async Task Api_AllowsConfiguredFrontendOrigin()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "Frontend:Origin",
                "https://frontend.example"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "https://frontend.example");

        using var response = await client.SendAsync(request);

        Assert.Equal(
            "https://frontend.example",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}