using Azure.Security.KeyVault.Secrets;

public interface IAppSecrets
{
    Task<string> ReadAsync(string name, CancellationToken ct);
}

public sealed class LocalAppSecrets(IConfiguration configuration) : IAppSecrets
{
    public Task<string> ReadAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(configuration[$"DevelopmentSecrets:{name}"]
            ?? throw new InvalidOperationException($"Missing non-sensitive DevelopmentSecrets:{name} test value."));
    }
}

public sealed class KeyVaultAppSecrets(SecretClient secrets) : IAppSecrets
{
    public async Task<string> ReadAsync(string name, CancellationToken ct)
        => (await secrets.GetSecretAsync(name, cancellationToken: ct)).Value.Value;
}