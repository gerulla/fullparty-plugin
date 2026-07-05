using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FullParty.Services;

internal sealed record ReadyCheckSummary(
    int Ready,
    int NotReady,
    int Waiting,
    int Missing,
    int Unknown,
    int Total,
    DateTimeOffset UpdatedAt)
{
    public string DisplayText
    {
        get
        {
            if (Total == 0)
                return "Ready check: no responses yet";

            var text = $"Ready {Ready}/{Total}";
            if (NotReady > 0)
                text += $", {NotReady} not ready";
            if (Waiting > 0)
                text += $", {Waiting} waiting";
            if (Missing > 0)
                text += $", {Missing} missing";
            if (Unknown > 0)
                text += $", {Unknown} unknown";

            return text;
        }
    }

    public bool IsComplete => Total > 0 && Waiting == 0 && Unknown == 0;
}

internal static unsafe class ReadyCheckStatusReader
{
    public static ReadyCheckSummary? Read()
    {
        var agent = AgentReadyCheck.Instance();
        if (agent == null)
            return null;

        var ready = 0;
        var notReady = 0;
        var waiting = 0;
        var missing = 0;
        var unknown = 0;
        var total = 0;

        foreach (var entry in agent->ReadyCheckEntries)
        {
            if (entry.ContentId == 0)
                continue;

            total++;
            switch (entry.Status)
            {
                case ReadyCheckStatus.Ready:
                    ready++;
                    break;
                case ReadyCheckStatus.NotReady:
                    notReady++;
                    break;
                case ReadyCheckStatus.AwaitingResponse:
                    waiting++;
                    break;
                case ReadyCheckStatus.MemberNotPresent:
                    missing++;
                    break;
                default:
                    unknown++;
                    break;
            }
        }

        return new ReadyCheckSummary(
            ready,
            notReady,
            waiting,
            missing,
            unknown,
            total,
            DateTimeOffset.UtcNow);
    }
}
