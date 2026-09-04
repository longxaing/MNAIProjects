using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_ReturnsSuccess() =>
        Assert.True((await _client.GetAsync("/health")).IsSuccessStatusCode);

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