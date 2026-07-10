using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnaiWork.Api.Infrastructure;

/// <summary>Shared System.Text.Json settings used for Cosmos, tool args, SSE and the API.</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
