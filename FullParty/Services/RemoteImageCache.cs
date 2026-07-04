using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FullParty.Auth;

namespace FullParty.Services;

public sealed class RemoteImageCache : IDisposable
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly HttpClient HttpClient = new();

    private readonly AuthService authService;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<string, string?> paths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string?>> tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> failures = new(StringComparer.Ordinal);

    public RemoteImageCache(AuthService authService)
    {
        this.authService = authService;
    }

    public string? GetImagePath(string? urlOrPath, string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath))
            return null;

        var url = authService.ResolveUrl(urlOrPath);
        if (paths.TryGetValue(url, out var path))
            return path;

        if (failures.TryGetValue(url, out var failedAt))
        {
            if (DateTimeOffset.UtcNow - failedAt < FailureRetryDelay)
                return null;

            failures.Remove(url);
        }

        if (!tasks.TryGetValue(url, out var task))
        {
            task = DownloadImageAsync(url, cacheKey, cancellation.Token);
            tasks[url] = task;
            return null;
        }

        if (!task.IsCompleted)
            return null;

        tasks.Remove(url);
        if (task.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(task.Result))
        {
            paths[url] = task.Result;
            failures.Remove(url);
            return task.Result;
        }

        failures[url] = DateTimeOffset.UtcNow;
        if (!task.IsCompletedSuccessfully)
            Plugin.Log.Warning(task.Exception, "FullParty image download failed.");

        return null;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static async Task<string?> DownloadImageAsync(string url, string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "FullParty Dalamud Plugin");
            request.Headers.TryAddWithoutValidation("Accept", "image/png,image/webp,image/jpeg,image/*,*/*");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxImageBytes)
                throw new InvalidOperationException("FullParty image is too large.");

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                throw new InvalidOperationException("FullParty image is empty or too large.");

            var cacheDirectory = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "images");
            Directory.CreateDirectory(cacheDirectory);

            var safeKey = MakeSafeCacheKey(cacheKey);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
            var extension = GetImageExtension(response.Content.Headers.ContentType?.MediaType, url);
            var filePath = Path.Combine(cacheDirectory, $"{safeKey}-{hash}{extension}");
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
            return filePath;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not download FullParty image from {ImageUrl}", url);
            return null;
        }
    }

    private static string MakeSafeCacheKey(string cacheKey)
    {
        var builder = new StringBuilder(cacheKey.Length);
        foreach (var c in cacheKey)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        return builder.Length == 0 ? "image" : builder.ToString();
    }

    private static string GetImageExtension(string? mediaType, string url)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => TryGetExtensionFromUrl(url) ?? ".png",
        };
    }

    private static string? TryGetExtensionFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" ? extension : null;
    }
}
