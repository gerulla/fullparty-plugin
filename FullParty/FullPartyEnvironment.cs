using System;
using System.Collections.Generic;
using System.IO;

namespace FullParty;

public sealed class FullPartyEnvironment
{
    public const string FileName = ".env";

    public string BaseUrl { get; }
    public string ClientId { get; }
    public bool Debug { get; }
    public string FilePath { get; }
    public bool FileExists { get; }

    private FullPartyEnvironment(string baseUrl, string clientId, bool debug, string filePath, bool fileExists)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        ClientId = clientId;
        Debug = debug;
        FilePath = filePath;
        FileExists = fileExists;
    }

    public static FullPartyEnvironment Load(string pluginDirectory)
    {
        var filePath = Path.Combine(pluginDirectory, FileName);
        var values = File.Exists(filePath) ? ReadEnvFile(filePath) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        values.TryGetValue("FULLPARTY_BASE_URL", out var baseUrl);
        values.TryGetValue("FULLPARTY_CLIENT_ID", out var clientId);
        values.TryGetValue("DEBUG", out var debugValue);

        return new FullPartyEnvironment(
            string.IsNullOrWhiteSpace(baseUrl) ? "https://fullparty.gg" : baseUrl.Trim(),
            clientId?.Trim() ?? string.Empty,
            bool.TryParse(debugValue, out var debug) && debug,
            filePath,
            File.Exists(filePath));
    }

    private static Dictionary<string, string> ReadEnvFile(string filePath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            values[key] = Unquote(value);
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];

        return value;
    }
}
