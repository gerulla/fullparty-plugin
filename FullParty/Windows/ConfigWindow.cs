using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FullParty.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("FullParty Debug###FullPartyDebug")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(520, 300);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        ImGui.Text("Settings");

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var auth = plugin.AuthService;
        var environment = plugin.Environment;
        ImGui.Text("Auth Debug");
        ImGui.TextWrapped($".env: {(environment.FileExists ? environment.FilePath : "not found")}");
        ImGui.Text($"Debug: {environment.Debug}");
        ImGui.Text($"State: {auth.State}");
        ImGui.TextWrapped($"Base URL: {auth.BaseUrl}");
        ImGui.TextWrapped($"Client ID: {MaskClientId(auth.ClientId)}");

        if (!string.IsNullOrWhiteSpace(auth.UserCode))
        {
            ImGui.Text($"User code: {auth.UserCode}");
        }

        if (!string.IsNullOrWhiteSpace(auth.VerificationUriComplete))
        {
            ImGui.TextWrapped($"Verification URL: {auth.VerificationUriComplete}");
        }
        else if (!string.IsNullOrWhiteSpace(auth.VerificationUri))
        {
            ImGui.TextWrapped($"Verification URL: {auth.VerificationUri}");
        }

        if (auth.DeviceCodeExpiresAt is { } expiresAt)
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            ImGui.TextDisabled($"Device code expires in {Math.Max(0, (int)remaining.TotalSeconds)}s");
        }

        if (auth.PollIntervalSeconds > 0)
        {
            ImGui.TextDisabled($"Polling every {auth.PollIntervalSeconds}s");
        }

        if (!string.IsNullOrWhiteSpace(auth.ErrorMessage))
        {
            ImGui.TextWrapped($"Error: {auth.ErrorMessage}");
        }

        if (auth.User != null)
        {
            ImGui.TextWrapped($"Logged in as: {auth.User.Name}");
        }
        else if (auth.PendingUser != null)
        {
            ImGui.TextWrapped($"Pending user: {auth.PendingUser.Name}");
        }

        ImGui.Spacing();
        if (ImGui.Button("Restart login"))
        {
            auth.Restart();
        }

        ImGui.SameLine();
        if (ImGui.Button("Sign out"))
        {
            auth.SignOut();
        }
    }

    private static string MaskClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return "not set";

        if (clientId.Length <= 8)
            return "set";

        return $"{clientId[..8]}...{clientId[^4..]}";
    }
}
