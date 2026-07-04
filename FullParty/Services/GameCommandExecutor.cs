using System;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FullParty.Services;

internal static unsafe class GameCommandExecutor
{
    public static void Execute(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || !command.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Only slash commands can be executed.", nameof(command));

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            throw new InvalidOperationException("The game UI module is not available.");

        using var textCommand = new Utf8String(command);
        uiModule->GetRaptureShellModule()->ExecuteCommandInner(&textCommand, uiModule);
    }
}
