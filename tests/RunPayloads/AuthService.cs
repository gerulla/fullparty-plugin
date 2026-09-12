using System.Text.Json;

namespace FullParty.Auth;

// Exercise the production API mapper with fixture responses, without credentials or network access.
public sealed class AuthService(string response)
{
    public string? LastPath { get; private set; }

    public Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        LastPath = path;
        return Task.FromResult(JsonSerializer.Deserialize<T>(response, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }));
    }

    public Task<T?> PostJsonAsync<T>(string path, object payload, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Read-only payload tests.");
}
