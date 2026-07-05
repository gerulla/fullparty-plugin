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

            return $"Ready {Ready}/{Total}, No {NotReady}, Waiting {Pending}";
        }
    }

    public int Pending => Math.Max(Waiting + Missing + Unknown, Total - Ready - NotReady);
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
            var status = entry.Status;
            var hasContentId = entry.ContentId != 0;
            var isPendingStatus = status is ReadyCheckStatus.AwaitingResponse or ReadyCheckStatus.MemberNotPresent;
            if (!hasContentId && !isPendingStatus)
                continue;

            total++;
            switch (status)
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
