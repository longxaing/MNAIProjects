using Microsoft.Extensions.Configuration;

namespace MnaiWork.Api.Configuration;

public sealed class DeploymentProfileConfigurationSource : IConfigurationSource
{
    private readonly IReadOnlyDictionary<string, string?> _initialData;

    public DeploymentProfileConfigurationSource(IReadOnlyDictionary<string, string?> initialData)
        => _initialData = initialData;

    public DeploymentProfileConfigurationProvider? Provider { get; private set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => Provider = new DeploymentProfileConfigurationProvider(_initialData);
}

public sealed class DeploymentProfileConfigurationProvider : ConfigurationProvider
{
    private readonly object _gate = new();

    public DeploymentProfileConfigurationProvider(IReadOnlyDictionary<string, string?> initialData)
        => Data = new Dictionary<string, string?>(initialData, StringComparer.OrdinalIgnoreCase);

    public void Update(IReadOnlyDictionary<string, string?> values)
    {
        lock (_gate)
        {
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        }
        OnReload();
    }
}