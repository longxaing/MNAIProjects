using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace MnaiWork.Api.Data;

/// <summary>
/// A <see cref="CosmosSerializer"/> backed by System.Text.Json so our POCO
/// <c>[JsonPropertyName]</c> attributes and enum-as-string settings are honored.
/// </summary>
public sealed class SystemTextJsonCosmosSerializer : CosmosSerializer
{
    private readonly JsonSerializerOptions _options;

    public SystemTextJsonCosmosSerializer(JsonSerializerOptions options) => _options = options;

    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            if (typeof(Stream).IsAssignableFrom(typeof(T)))
            {
                return (T)(object)stream;
            }

            if (stream.CanSeek && stream.Length == 0)
            {
                return default!;
            }

            return JsonSerializer.Deserialize<T>(stream, _options)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, input, _options);
        stream.Position = 0;
        return stream;
    }
}
