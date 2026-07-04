using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FullParty.Auth;

public sealed class AuthService : IDisposable
{
    private const string Scope = "xivplugin:read";
    private static readonly TimeSpan AccessTokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly Configuration configuration;
    private readonly FullPartyEnvironment environment;
    private readonly HttpClient httpClient = new();
    private readonly object stateLock = new();
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private CancellationTokenSource? authCancellation;
    private Task? authTask;
    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt;
    private bool hasAutoStarted;

    public AuthState State { get; private set; } = AuthState.SignedOut;
    public string? ErrorMessage { get; private set; }
    public string? UserCode { get; private set; }
    public string? VerificationUri { get; private set; }
    public string? VerificationUriComplete { get; private set; }
    public DateTimeOffset? DeviceCodeExpiresAt { get; private set; }
    public int PollIntervalSeconds { get; private set; }
    public FullPartyUser? PendingUser { get; private set; }
    public FullPartyUser? User { get; private set; }

    public AuthService(Configuration configuration, FullPartyEnvironment environment)
    {
        this.configuration = configuration;
        this.environment = environment;
    }

    public string BaseUrl => environment.BaseUrl;
    public string ClientId => environment.ClientId;
    public bool Debug => environment.Debug;

    public string ResolveUrl(string urlOrPath)
    {
        if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var absoluteUri))
            return absoluteUri.ToString();

        var baseUri = new Uri(BaseUrl.EndsWith('/') ? BaseUrl : $"{BaseUrl}/");
        return new Uri(baseUri, urlOrPath.TrimStart('/')).ToString();
    }

    public async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var bearerToken = GetValidAccessToken();

        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveUrl(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Plugin.Log.Warning("FullParty API GET {Path} failed: {StatusCode} {Content}", path, response.StatusCode, content);
            throw new InvalidOperationException($"FullParty API request failed ({(int)response.StatusCode}).");
        }

        return JsonSerializer.Deserialize<T>(content, jsonOptions);
    }

    public async Task<T?> PostJsonAsync<T>(string path, object payload, CancellationToken cancellationToken)
    {
        var bearerToken = GetValidAccessToken();
        var json = JsonSerializer.Serialize(payload, jsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveUrl(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Plugin.Log.Warning("FullParty API POST {Path} failed: {StatusCode} {Content}", path, response.StatusCode, content);
            throw new InvalidOperationException($"FullParty API request failed ({(int)response.StatusCode}).");
        }

        return JsonSerializer.Deserialize<T>(content, jsonOptions);
    }

    public void EnsureStarted()
    {
        lock (stateLock)
        {
            if (authTask is { IsCompleted: false })
                return;

            if (State == AuthState.Authenticated && DateTimeOffset.UtcNow < accessTokenExpiresAt - AccessTokenRefreshMargin)
                return;

            if (State is AuthState.Refreshing or AuthState.RequestingDeviceCode or AuthState.WaitingForApproval or AuthState.VerifyingUser or AuthState.ReadyToFinish)
                return;

            if (State == AuthState.Error)
                return;

            if (State == AuthState.SignedOut)
            {
                if (hasAutoStarted)
                    return;

                hasAutoStarted = true;
            }

            authCancellation?.Cancel();
            authCancellation?.Dispose();
            authCancellation = new CancellationTokenSource();
            authTask = RunAuthenticationAsync(authCancellation.Token);
        }
    }

    public void RestoreSavedSession()
    {
        lock (stateLock)
        {
            if (authTask is { IsCompleted: false })
                return;

            if (State == AuthState.Authenticated && DateTimeOffset.UtcNow < accessTokenExpiresAt - AccessTokenRefreshMargin)
                return;

            if (State is AuthState.Refreshing or AuthState.RequestingDeviceCode or AuthState.WaitingForApproval or AuthState.VerifyingUser or AuthState.ReadyToFinish)
                return;

            if (string.IsNullOrWhiteSpace(configuration.ProtectedRefreshToken))
                return;

            authCancellation?.Cancel();
            authCancellation?.Dispose();
            authCancellation = new CancellationTokenSource();
            authTask = RestoreSavedSessionAsync(authCancellation.Token);
        }
    }

    public void Restart()
    {
        lock (stateLock)
        {
            authCancellation?.Cancel();
            authTask = null;
            accessToken = null;
            accessTokenExpiresAt = default;
            PendingUser = null;
            User = null;
            ClearDeviceCodeState();
            ErrorMessage = null;
            State = AuthState.SignedOut;
            hasAutoStarted = false;
        }

        EnsureStarted();
    }

    public void SignOut()
    {
        lock (stateLock)
        {
            authCancellation?.Cancel();
            authTask = null;
            accessToken = null;
            accessTokenExpiresAt = default;
            PendingUser = null;
            User = null;
            ClearDeviceCodeState();
            ErrorMessage = null;
            State = AuthState.SignedOut;
            hasAutoStarted = true;
            configuration.ProtectedRefreshToken = null;
            configuration.Save();
        }
    }

    public void FinishLogin()
    {
        lock (stateLock)
        {
            if (PendingUser == null)
                return;

            User = PendingUser;
            PendingUser = null;
            ClearDeviceCodeState();
            ErrorMessage = null;
            State = AuthState.Authenticated;
        }
    }

    public bool OpenApprovalPage()
    {
        var url = VerificationUriComplete ?? VerificationUri;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            SetError($"Could not open browser: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        authCancellation?.Cancel();
        authCancellation?.Dispose();
        httpClient.Dispose();
    }

    private async Task RunAuthenticationAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(environment.ClientId))
            {
                SetError("Set FULLPARTY_CLIENT_ID in the FullParty .env file.");
                return;
            }

            var protectedRefreshToken = configuration.ProtectedRefreshToken;
            if (!string.IsNullOrWhiteSpace(protectedRefreshToken))
            {
                SetState(AuthState.Refreshing);
                var refreshToken = TryUnprotectRefreshToken(protectedRefreshToken);
                if (!string.IsNullOrWhiteSpace(refreshToken) && await RefreshAsync(refreshToken, cancellationToken))
                    return;
            }

            await StartDeviceFlowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            Plugin.Log.Error(ex, "FullParty authentication failed.");
        }
    }

    private string GetValidAccessToken()
    {
        lock (stateLock)
        {
            if (string.IsNullOrWhiteSpace(accessToken) || DateTimeOffset.UtcNow >= accessTokenExpiresAt - AccessTokenRefreshMargin)
            {
                EnsureStarted();
                throw new InvalidOperationException("FullParty session is refreshing. Try again in a moment.");
            }

            return accessToken;
        }
    }

    private async Task RestoreSavedSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(environment.ClientId))
            {
                SetError("Set FULLPARTY_CLIENT_ID in the FullParty .env file.");
                return;
            }

            var protectedRefreshToken = configuration.ProtectedRefreshToken;
            if (string.IsNullOrWhiteSpace(protectedRefreshToken))
                return;

            SetState(AuthState.Refreshing);
            var refreshToken = TryUnprotectRefreshToken(protectedRefreshToken);
            if (string.IsNullOrWhiteSpace(refreshToken) || !await RefreshAsync(refreshToken, cancellationToken))
                SetState(AuthState.SignedOut);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            Plugin.Log.Error(ex, "FullParty saved-session restore failed.");
        }
    }

    private async Task StartDeviceFlowAsync(CancellationToken cancellationToken)
    {
        SetState(AuthState.RequestingDeviceCode);

        var response = await PostFormAsync<DeviceCodeResponse>("/oauth/device/code", new Dictionary<string, string>
        {
            ["client_id"] = environment.ClientId,
            ["scope"] = Scope,
        }, cancellationToken);

        if (response == null || string.IsNullOrWhiteSpace(response.DeviceCode))
        {
            SetError("FullParty did not return a device code.");
            return;
        }

        lock (stateLock)
        {
            UserCode = response.UserCode;
            VerificationUri = response.VerificationUri;
            VerificationUriComplete = response.VerificationUriComplete;
            DeviceCodeExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);
            PollIntervalSeconds = Math.Max(1, response.Interval);
            ErrorMessage = null;
            State = AuthState.WaitingForApproval;
        }

        await PollForTokenAsync(response.DeviceCode, cancellationToken);
    }

    private async Task PollForTokenAsync(string deviceCode, CancellationToken cancellationToken)
    {
        var interval = Math.Max(1, PollIntervalSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);

            var tokenResponse = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = environment.ClientId,
                ["device_code"] = deviceCode,
            }, cancellationToken);

            if (tokenResponse == null)
                return;

            switch (tokenResponse.Error)
            {
                case null:
                    await AcceptTokenAsync(tokenResponse, cancellationToken, true);
                    return;
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    lock (stateLock)
                    {
                        PollIntervalSeconds = interval;
                    }

                    continue;
                case "access_denied":
                    SetError("Access was denied on FullParty.gg.");
                    return;
                case "expired_token":
                    SetError("The FullParty login code expired. Try again.");
                    return;
                default:
                    SetError(tokenResponse.ErrorDescription ?? tokenResponse.Error);
                    return;
            }
        }
    }

    private async Task<bool> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var tokenResponse = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = environment.ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        }, cancellationToken);

        if (tokenResponse == null || !string.IsNullOrWhiteSpace(tokenResponse.Error))
        {
            configuration.ProtectedRefreshToken = null;
            configuration.Save();
            SetState(AuthState.SignedOut);
            return false;
        }

        await AcceptTokenAsync(tokenResponse, cancellationToken, false);
        return State == AuthState.Authenticated;
    }

    private async Task AcceptTokenAsync(TokenResponse tokenResponse, CancellationToken cancellationToken, bool requireFinish)
    {
        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            SetError("FullParty did not return an access token.");
            return;
        }

        lock (stateLock)
        {
            accessToken = tokenResponse.AccessToken;
            accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, tokenResponse.ExpiresIn));
            ErrorMessage = null;
            State = AuthState.VerifyingUser;
        }

        if (!string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
        {
            configuration.ProtectedRefreshToken = ProtectRefreshToken(tokenResponse.RefreshToken);
            configuration.Save();
        }

        await LoadUserAsync(cancellationToken, requireFinish);
    }

    private async Task LoadUserAsync(CancellationToken cancellationToken, bool requireFinish)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || DateTimeOffset.UtcNow >= accessTokenExpiresAt - AccessTokenRefreshMargin)
        {
            SetState(AuthState.SignedOut);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/xivplugin/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            SetError($"Could not verify FullParty account ({(int)response.StatusCode}).");
            Plugin.Log.Warning("FullParty /me failed: {StatusCode} {Content}", response.StatusCode, content);
            return;
        }

        var me = JsonSerializer.Deserialize<MeResponse>(content, jsonOptions);
        if (me?.User == null)
        {
            SetError("FullParty returned an empty user profile.");
            return;
        }

        lock (stateLock)
        {
            if (requireFinish)
            {
                PendingUser = me.User;
                User = null;
                State = AuthState.ReadyToFinish;
            }
            else
            {
                User = me.User;
                PendingUser = null;
                State = AuthState.Authenticated;
            }

            ClearDeviceCodeState();
            ErrorMessage = null;
        }
    }

    private async Task<T?> PostFormAsync<T>(string path, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await httpClient.PostAsync($"{BaseUrl}{path}", content, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            SetError($"FullParty request failed ({(int)response.StatusCode}).");
            Plugin.Log.Warning("FullParty request to {Path} failed: {StatusCode} {Content}", path, response.StatusCode, responseContent);
            return default;
        }

        return JsonSerializer.Deserialize<T>(responseContent, jsonOptions);
    }

    private async Task<TokenResponse?> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await httpClient.PostAsync($"{BaseUrl}/oauth/token", content, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Forbidden)
            return JsonSerializer.Deserialize<TokenResponse>(responseContent, jsonOptions);

        if (!response.IsSuccessStatusCode)
        {
            SetError($"FullParty token request failed ({(int)response.StatusCode}).");
            Plugin.Log.Warning("FullParty token request failed: {StatusCode} {Content}", response.StatusCode, responseContent);
            return null;
        }

        return JsonSerializer.Deserialize<TokenResponse>(responseContent, jsonOptions);
    }

    private string ProtectRefreshToken(string refreshToken)
    {
        var bytes = Encoding.UTF8.GetBytes(refreshToken);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private string? TryUnprotectRefreshToken(string protectedRefreshToken)
    {
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedRefreshToken);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not decrypt stored FullParty refresh token.");
            return null;
        }
    }

    private void SetState(AuthState state)
    {
        lock (stateLock)
        {
            ErrorMessage = null;
            State = state;
        }
    }

    private void SetError(string message)
    {
        lock (stateLock)
        {
            ErrorMessage = message;
            State = AuthState.Error;
        }
    }

    private void ClearDeviceCodeState()
    {
        UserCode = null;
        VerificationUri = null;
        VerificationUriComplete = null;
        DeviceCodeExpiresAt = null;
        PollIntervalSeconds = 0;
    }

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = string.Empty;

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = string.Empty;

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; } = string.Empty;

        [JsonPropertyName("verification_uri_complete")]
        public string VerificationUriComplete { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }

    private sealed class MeResponse
    {
        [JsonPropertyName("user")]
        public FullPartyUser? User { get; set; }
    }
}
