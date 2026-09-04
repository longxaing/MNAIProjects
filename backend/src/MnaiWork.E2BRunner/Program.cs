using System.Text.Json;
using MnaiWork.BuildExecution;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 24 * 1024 * 1024;
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapPost("/build", async (
    BuildProjectRequest request,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BuildExecution:Enabled"] = "true",
            ["BuildExecution:MaxConcurrentBuilds"] = "1",
            ["BuildExecution:CommandTimeoutMinutes"] = request.CommandTimeoutMinutes.ToString(),
            ["BuildExecution:TotalTimeoutMinutes"] = request.TotalTimeoutMinutes.ToString(),
            ["BuildExecution:PlaywrightVersion"] = request.PlaywrightVersion
        })
        .Build();
    var pipeline = new LocalBuildPipeline(
        configuration,
        loggerFactory.CreateLogger<LocalBuildPipeline>());

    try
    {
        return Results.Ok(await pipeline.ExecuteAsync(request, ct));
    }
    catch (Exception ex) when (ex is InvalidDataException
        or InvalidOperationException
        or JsonException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();