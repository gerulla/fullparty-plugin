using System;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FullParty.Services;

internal static class ReadyCheckSoundPlayer
{
    // Dalamud uses this same UI cue when a window opens; it matches the ready-check prompt cue.
    private const uint ReadyCheckStartedSoundEffectId = 23;
    // These are the two sounds used by the game's "Ready check complete" SeString.
    private const uint AllReadySoundEffectId = 46;
    private const uint DeclinedSoundEffectId = 47;
    private const uint AllianceReadyChatSoundEffectId = 6;

    public static void PlayPartyLeadStarted(Configuration configuration)
    {
        if (!configuration.MutePartyLeadReadyCheck)
            PlaySystem(ReadyCheckStartedSoundEffectId, "started");
    }

    public static void PlayPartyLeadResult(Configuration configuration, bool ready)
    {
        if (!configuration.MutePartyLeadReadyCheck)
            PlayResult(ready, "raid-lead");
    }

    public static void PlayAlliancePartyResult(Configuration configuration, bool ready)
    {
        if (!configuration.MuteAlliancePerPartyReadyCheck)
            PlayResult(ready, "alliance party");
    }

    public static unsafe void PlayAllianceReady(Configuration configuration)
    {
        if (configuration.MuteAllianceAllReadySound)
            return;

        try
        {
            UIGlobals.PlayChatSoundEffect(AllianceReadyChatSoundEffectId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not play the FullParty alliance-ready bongos.");
        }
    }

    private static void PlayResult(bool ready, string source) =>
        PlaySystem(ready ? AllReadySoundEffectId : DeclinedSoundEffectId, $"{source} {(ready ? "success" : "failure")}");

    private static unsafe void PlaySystem(uint soundEffectId, string result)
    {
        try
        {
            UIGlobals.PlaySoundEffect(soundEffectId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not play the FullParty ready-check {Result} sound.", result);
        }
    }
}