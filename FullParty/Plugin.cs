using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using FullParty.Api;
using FullParty.Auth;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using ElezenTools;
using ElezenTools.UI;
using FullParty.Models;
using FullParty.Services;
using FullParty.Windows;

namespace FullParty;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;

    private const string CommandName = "/fullparty";

    public Configuration Configuration { get; init; }
    public FullPartyEnvironment Environment { get; init; }
    public AuthService AuthService { get; init; }
    public FullPartyApiClient ApiClient { get; init; }
    public RemoteImageCache ImageCache { get; init; }
    internal AdventurerListService AdventurerList { get; init; }
    public LiveRoomManager LiveRoomManager { get; init; }
    public OccultCrescentRunMonitor OccultCrescentRunMonitor { get; init; }
    public string VersionText { get; init; }

    public readonly WindowSystem WindowSystem = new("FullParty");
    private ConfigWindow ConfigWindow { get; init; }
    private SettingsWindow SettingsWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private LiveRoomStatusOverlay LiveRoomStatusOverlay { get; init; }
    private readonly List<RunWindow> runWindows = [];
    private readonly List<ApplicationWindow> applicationWindows = [];

    public Plugin()
    {
        ElezenInit.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Environment = FullPartyEnvironment.Load(PluginInterface.AssemblyLocation.Directory?.FullName ?? AppContext.BaseDirectory);
        AuthService = new AuthService(Configuration, Environment);
        AuthService.RestoreSavedSession();
        ApiClient = new FullPartyApiClient(AuthService);
        ImageCache = new RemoteImageCache(AuthService);
        AdventurerList = new AdventurerListService();
        LiveRoomManager = new LiveRoomManager(this);
        OccultCrescentRunMonitor = new OccultCrescentRunMonitor(this);
        VersionText = PluginInterface.Manifest.AssemblyVersion?.ToString() ?? "dev";
        ClassJobResolver.WarmUp();
        PhantomJobResolver.WarmUp();

        ConfigWindow = new ConfigWindow(this);
        SettingsWindow = new SettingsWindow(this);
        MainWindow = new MainWindow(this);
        LiveRoomStatusOverlay = new LiveRoomStatusOverlay(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(SettingsWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(LiveRoomStatusOverlay);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open FullParty. Use /fullparty debug for settings and auth debug info, or /fullparty players to dump party/alliance data."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += DrawUi;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [FullParty] ===A cool log message from FullParty===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        OccultCrescentRunMonitor.Dispose();

        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        LiveRoomStatusOverlay.Dispose();
        foreach (var runWindow in runWindows)
        {
            runWindow.Dispose();
        }

        foreach (var applicationWindow in applicationWindows)
        {
            applicationWindow.Dispose();
        }

        ImageCache.Dispose();
        AdventurerList.Dispose();
        LiveRoomManager.Dispose();
        AuthService.Dispose();

        CommandManager.RemoveHandler(CommandName);
        ElezenInit.Dispose();
    }

    private void DrawUi()
    {
        using var theme = ModernTheme.Push(FullPartyModernPalette.Value);
        using var style = FullPartyModernPalette.PushImGuiStyle();
        WindowSystem.Draw();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        if (args.Trim().Equals("players", StringComparison.OrdinalIgnoreCase))
        {
            PartyListDebugDumper.Dump();
            return;
        }

        ToggleMainUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleSettingsUi() => SettingsWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    internal static void ShowErrorToast(string message)
    {
        NotificationManager.AddNotification(new Notification
        {
            Title = "FullParty",
            Content = message,
            Type = NotificationType.Error,
        });
    }

    public void OpenRunWindow(FullPartyRun run)
    {
        var existing = runWindows.Find(window => window.Run.Id == run.Id);
        if (existing != null)
        {
            existing.IsOpen = true;
            return;
        }

        var runWindow = new RunWindow(run, this);
        runWindows.Add(runWindow);
        WindowSystem.AddWindow(runWindow);
    }

    public void OpenApplicationWindow(FullPartyRun run, FullPartyRosterSlot slot)
    {
        if (slot.ApplicationId == null)
            return;

        var existing = applicationWindows.Find(window => window.RunId == run.Id && window.SlotId == slot.Id);
        if (existing != null)
        {
            existing.IsOpen = true;
            return;
        }

        var title = slot.AssignedCharacter?.Name ?? slot.SlotLabel;
        var applicationWindow = new ApplicationWindow(run.Id, slot.Id, title, this);
        applicationWindows.Add(applicationWindow);
        WindowSystem.AddWindow(applicationWindow);
    }
}
