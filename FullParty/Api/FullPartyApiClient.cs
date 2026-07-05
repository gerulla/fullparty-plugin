using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FullParty.Auth;
using FullParty.Models;

namespace FullParty.Api;

public sealed class FullPartyApiClient
{
    private readonly AuthService authService;

    public FullPartyApiClient(AuthService authService)
    {
        this.authService = authService;
    }

    public async Task<IReadOnlyList<FullPartyGroup>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        var response = await authService.GetJsonAsync<GroupsResponse>("/api/xivplugin/groups", cancellationToken);
        return response?.Data.Where(group => group.CanModerate).Select(group => new FullPartyGroup(
            group.Id,
            group.Slug,
            group.Name,
            group.ProfilePictureUrl,
            group.BannerImageUrl,
            group.Datacenter,
            group.Role,
            group.CanModerate)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<FullPartyRun>> GetGroupRunsAsync(string groupSlug, CancellationToken cancellationToken)
    {
        var response = await authService.GetJsonAsync<GroupRunsResponse>($"/api/xivplugin/groups/{Uri.EscapeDataString(groupSlug)}/runs", cancellationToken);
        var canModerate = response?.Group?.CanModerate;
        return response?.Data.Select(run => MapRun(run, canModerate)).ToList() ?? [];
    }

    public async Task<FullPartyRunDetail?> GetRunDetailAsync(int runId, CancellationToken cancellationToken)
    {
        var response = await authService.GetJsonAsync<RunDetailResponse>($"/api/xivplugin/runs/{runId}", cancellationToken);
        if (response?.Data == null)
            return null;

        var run = response.Data;
        return new FullPartyRunDetail(
            run.Id,
            run.GroupId,
            run.Status,
            run.StartsAt,
            run.DurationMinutes,
            run.ApplicationCount,
            run.CanModerate,
            run.Roster?.Slots.Select(MapRosterSlot).ToList() ?? []);
    }

    public async Task<FullPartyApplication?> GetSlotApplicationAsync(int runId, int slotId, CancellationToken cancellationToken)
    {
        var response = await authService.GetJsonAsync<ApplicationResponse>($"/api/xivplugin/runs/{runId}/slots/{slotId}/application", cancellationToken);
        if (response?.Data == null)
            return null;

        var application = response.Data;
        return new FullPartyApplication(
            application.Id,
            application.ActivityId,
            application.Status,
            application.Notes,
            application.ReviewReason,
            application.SubmittedAt,
            application.ReviewedAt,
            application.User?.Name ?? "Unknown user",
            application.User?.AvatarUrl,
            application.SelectedCharacter == null
                ? null
                : new FullPartyRosterCharacter(
                    application.SelectedCharacter.Id ?? 0,
                    application.SelectedCharacter.Name,
                    application.SelectedCharacter.World,
                    application.SelectedCharacter.Datacenter,
                    application.SelectedCharacter.AvatarUrl,
                    application.User?.Name),
            MapApplicationDetails(application.Details),
            application.Answers.Select(answer => new FullPartyApplicationAnswer(
                answer.QuestionKey,
                answer.QuestionLabel?.En ?? answer.QuestionKey,
                answer.QuestionType,
                FormatAnswerValue(answer.Value))).ToList());
    }

    public async Task<FullPartyRealtimeConfig> GetRealtimeConfigAsync(CancellationToken cancellationToken)
    {
        var response = await authService.GetJsonAsync<JsonElement>("/api/xivplugin/realtime", cancellationToken);
        var root = response.ValueKind == JsonValueKind.Object && TryGetProperty(response, "data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : response;

        var appKey = FindConfigString(root, ["key", "app_key", "appKey", "pusher_key", "reverb_key"]);
        var host = FindConfigString(root, ["host", "ws_host", "wsHost", "reverb_host", "pusher_host"]);

        if (string.IsNullOrWhiteSpace(appKey))
            throw new InvalidOperationException("FullParty realtime config did not include a Reverb app key.");

        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("FullParty realtime config did not include a websocket host.");

        var scheme = FindConfigString(root, ["scheme", "protocol"]);
        var forceTls = FindConfigBool(root, ["force_tls", "forceTLS", "encrypted", "use_tls", "useTLS"]) ??
                       (scheme?.Contains("https", StringComparison.OrdinalIgnoreCase) == true ||
                        scheme?.Contains("wss", StringComparison.OrdinalIgnoreCase) == true);

        return new FullPartyRealtimeConfig(
            appKey,
            host,
            scheme,
            FindConfigInt(root, ["ws_port", "wsPort"]) ?? (!forceTls ? FindConfigInt(root, ["port"]) : null),
            FindConfigInt(root, ["wss_port", "wssPort"]) ?? (forceTls ? FindConfigInt(root, ["port"]) : null),
            forceTls,
            FindConfigString(root, ["auth_endpoint", "authEndpoint", "broadcasting_auth_endpoint", "broadcastAuthEndpoint"]) ?? "/api/xivplugin/broadcasting/auth",
            FindChannelPattern(root) ?? "presence-xivplugin.runs.{run_id}",
            FindConfigString(root, ["path", "ws_path", "wsPath"]),
            FindEventName(root, ["command", "run_command"]) ?? "xivplugin.run.command",
            FindEventName(root, ["command_acknowledged", "commandAcknowledged", "acknowledged", "run_command_acknowledged"]) ?? "xivplugin.run.command.acknowledged",
            FindEventName(root, ["party_snapshot", "partySnapshot", "run_party_snapshot"]) ?? "xivplugin.run.party_snapshot");
    }

    public async Task<FullPartyBroadcastAuth> AuthorizeRealtimeChannelAsync(string authEndpoint, string socketId, string channelName, CancellationToken cancellationToken)
    {
        var response = await authService.PostJsonAsync<BroadcastAuthResponse>(
            authEndpoint,
            new BroadcastAuthRequest(socketId, channelName),
            cancellationToken);

        if (response == null || string.IsNullOrWhiteSpace(response.Auth))
            throw new InvalidOperationException("FullParty did not authorize the live room channel.");

        var channelData = response.ChannelData.ValueKind switch
        {
            JsonValueKind.String => response.ChannelData.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => response.ChannelData.GetRawText(),
            _ => null,
        };

        return new FullPartyBroadcastAuth(response.Auth, channelData);
    }

    public async Task SendRunCommandAsync(
        int runId,
        string command,
        string targetType,
        object payload,
        int expiresInSeconds,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = await authService.PostJsonAsync<JsonElement>(
            $"/api/xivplugin/runs/{runId}/commands",
            new RunCommandRequest(
                command,
                new RunCommandTarget(targetType),
                payload,
                expiresInSeconds,
                idempotencyKey),
            cancellationToken);
    }

    public async Task AcknowledgeRunCommandAsync(int runId, string commandId, string status, CancellationToken cancellationToken)
    {
        _ = await authService.PostJsonAsync<JsonElement>(
            $"/api/xivplugin/runs/{runId}/commands/{Uri.EscapeDataString(commandId)}/ack",
            new RunCommandAckRequest(status),
            cancellationToken);
    }

    public async Task SendPartySnapshotAsync(int runId, FullPartyPartySnapshot snapshot, CancellationToken cancellationToken)
    {
        _ = await authService.PostJsonAsync<JsonElement>(
            $"/api/xivplugin/runs/{runId}/party-snapshot",
            new PartySnapshotRequest(
                snapshot.Sequence,
                snapshot.PartyKey,
                snapshot.Members.Select(member => new PartySnapshotMemberRequest(
                    member.Position,
                    member.CharacterId,
                    string.IsNullOrWhiteSpace(member.Name) ? null : member.Name,
                    string.IsNullOrWhiteSpace(member.World) ? null : member.World,
                    member.ClassJobId,
                    member.PhantomJobId)).ToList()),
            cancellationToken);
    }

    public async Task SubmitRunCheckInsAsync(
        int runId,
        IReadOnlyList<int> slotIds,
        IReadOnlyList<long> characterIds,
        CancellationToken cancellationToken)
    {
        _ = await authService.PostJsonAsync<JsonElement>(
            $"/api/xivplugin/runs/{runId}/check-ins",
            new RunCheckInsRequest(slotIds, characterIds),
            cancellationToken);
    }

    private static FullPartyRun MapRun(RunDto run, bool? canModerate)
    {
        return new FullPartyRun(
            run.Id,
            run.GroupId,
            run.Status,
            run.Name,
            string.IsNullOrWhiteSpace(run.Title) ? run.Name : run.Title,
            run.StartsAt,
            run.EndsAt,
            run.DurationMinutes,
            run.Datacenter,
            run.IsPublic,
            run.NeedsApplication,
            run.ApplicationCount,
            run.ActivityType == null
                ? null
                : new FullPartyActivity(
                    run.ActivityType.Id,
                    run.ActivityType.DisplayName,
                    run.ActivityType.Difficulty,
                    run.ActivityType.SmallImageUrl,
                    run.ActivityType.BannerImageUrl),
            canModerate);
    }

    private static FullPartyRosterSlot MapRosterSlot(RosterSlotDto slot)
    {
        var classValue = slot.FieldValues.FirstOrDefault(field => field.FieldKey == "character_class")?.Value;
        var phantomJobValue = slot.FieldValues.FirstOrDefault(field => field.FieldKey.Equals("phantom_job", StringComparison.OrdinalIgnoreCase))?.Value;
        return new FullPartyRosterSlot(
            slot.Id,
            slot.GroupKey,
            slot.GroupLabel?.En ?? FormatGroupLabel(slot.GroupKey),
            slot.SlotKey,
            slot.SlotLabel?.En ?? slot.SlotKey,
            slot.PositionInGroup,
            slot.SortOrder,
            slot.IsHost,
            slot.IsRaidLeader,
            slot.Assignment?.ApplicationId,
            slot.Assignment?.Source,
            slot.Assignment?.AttendanceStatus,
            slot.AssignedCharacter == null
                ? null
                : new FullPartyRosterCharacter(
                    slot.AssignedCharacter.Id,
                    slot.AssignedCharacter.Name,
                    slot.AssignedCharacter.World,
                    slot.AssignedCharacter.Datacenter,
                    slot.AssignedCharacter.AvatarUrl,
                    slot.AssignedCharacter.User?.Name),
            classValue?.Id,
            classValue?.Shorthand ?? classValue?.Name,
            classValue?.Role,
            phantomJobValue?.Id,
            phantomJobValue?.Name,
            phantomJobValue?.MaxLevel,
            phantomJobValue?.IconId,
            GetPhantomJobIconUrl(phantomJobValue),
            GetPhantomJobIconUrls(phantomJobValue));
    }

    private static FullPartyApplicationDetails? MapApplicationDetails(ApplicationDetailsDto? details)
    {
        if (details == null)
            return null;

        return new FullPartyApplicationDetails(
            details.User == null
                ? null
                : new FullPartyApplicationUser(
                    details.User.Id,
                    details.User.Name ?? "Unknown user",
                    details.User.AvatarUrl),
            MapApplicationCharacter(details.ApplicantCharacter),
            MapApplicationCharacter(details.SelectedCharacter),
            details.Answers.Select(MapDetailedAnswer).ToList(),
            details.ProgressMilestones.Select(milestone => new FullPartyProgressMilestone(
                milestone.Key,
                milestone.Label?.En ?? FormatKeyLabel(milestone.Key),
                milestone.Reached,
                milestone.Kills,
                milestone.ProgressPercent)).ToList(),
            details.UserStats == null
                ? null
                : new FullPartyApplicationUserStats(
                    details.UserStats.GroupRunCount,
                    details.UserStats.OverallRunCount,
                    MapStatBucket(details.UserStats.Class),
                    MapStatBucket(details.UserStats.PhantomJob)));
    }

    private static FullPartyApplicationCharacter? MapApplicationCharacter(ApplicationCharacterDto? character)
    {
        if (character == null)
            return null;

        return new FullPartyApplicationCharacter(
            character.Id,
            character.LodestoneId,
            character.Name,
            character.World,
            character.Datacenter,
            character.AvatarUrl,
            character.IsClaimed,
            character.LodestoneLastCheckedAt ?? character.LodestoneRefreshedAt,
            character.OccultLevel,
            character.PhantomMastery,
            character.BloodProgress == null
                ? null
                : new FullPartyBloodProgress(
                    character.BloodProgress.Clears,
                    character.BloodProgress.DataSource,
                    character.BloodProgress.Bosses.Select(boss => new FullPartyBloodBossProgress(
                        boss.Key,
                        boss.Kills,
                        boss.ProgressPercent ?? boss.Progress)).ToList()));
    }

    private static FullPartyApplicationDetailedAnswer MapDetailedAnswer(ApplicationAnswerDto answer)
    {
        var formattedValue = FormatAnswerValue(answer.Value ?? answer.RawValue);
        var displayValues = answer.DisplayValues.Count > 0
            ? answer.DisplayValues
            : !string.IsNullOrWhiteSpace(formattedValue)
                ? [formattedValue]
                : [];

        return new FullPartyApplicationDetailedAnswer(
            answer.QuestionKey,
            answer.QuestionLabel?.En ?? answer.QuestionKey,
            answer.QuestionType,
            answer.Source,
            displayValues,
            answer.RoleValues,
            answer.DisplayItems.Select(item => new FullPartyApplicationDisplayItem(
                item.Label,
                item.Role,
                item.IconUrl,
                item.FlatIconUrl,
                item.TransparentIconUrl)).ToList());
    }

    private static FullPartyApplicationStatBucket? MapStatBucket(ApplicationStatBucketDto? bucket)
    {
        if (bucket == null)
            return null;

        return new FullPartyApplicationStatBucket(
            bucket.Group.Select(MapStatItem).ToList(),
            bucket.Overall.Select(MapStatItem).ToList());
    }

    private static FullPartyApplicationStatItem MapStatItem(ApplicationStatItemDto item)
    {
        return new FullPartyApplicationStatItem(
            item.Key,
            item.Label,
            item.Role,
            item.IconUrl,
            item.FlatIconUrl,
            item.TransparentIconUrl,
            item.Count);
    }

    private static string FormatGroupLabel(string groupKey)
    {
        var suffix = groupKey.Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(suffix) ? groupKey : $"Party {suffix.ToUpperInvariant()}";
    } 

    private static string FormatKeyLabel(string key)
    {
        return string.Join(" ", key.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string? GetPhantomJobIconUrl(SlotFieldValueDto? value)
    {
        return GetPhantomJobIconUrls(value).FirstOrDefault();
    }

    private static IReadOnlyList<string> GetPhantomJobIconUrls(SlotFieldValueDto? value)
    {
        return new[]
        {
            value?.TransparentIconUrl,
            value?.IconUrl,
            value?.BlackIconUrl,
            value?.SpriteUrl,
            value?.SmallImageUrl,
            value?.ImageUrl,
        }.Where(url => !string.IsNullOrWhiteSpace(url))
         .Select(url => url!)
         .Distinct(StringComparer.Ordinal)
         .ToArray();
    }

    private static string? GetFieldDisplayName(SlotFieldValueDto? value)
    {
        return value?.Shorthand ?? value?.Name ?? value?.Key ?? value?.Label?.En;
    }

    private static string? FormatAnswerValue(JsonElement? value)
    {
        if (value == null)
            return null;

        return FormatAnswerValue(value.Value);
    }

    private static string? FormatAnswerValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => FormatAnswerObject(value),
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(FormatAnswerValue).Where(answer => !string.IsNullOrWhiteSpace(answer))),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => value.ToString(),
        };
    }

    private static string? FormatAnswerObject(JsonElement value)
    {
        if (value.TryGetProperty("label", out var label))
        {
            var labelValue = FormatLocalizedString(label);
            if (!string.IsNullOrWhiteSpace(labelValue))
                return labelValue;
        }

        foreach (var property in new[] { "name", "key", "shorthand", "id" })
        {
            if (value.TryGetProperty(property, out var propertyValue))
            {
                var formatted = FormatAnswerValue(propertyValue);
                if (!string.IsNullOrWhiteSpace(formatted))
                    return formatted;
            }
        }

        return value.ToString();
    }

    private static string? FormatLocalizedString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var language in new[] { "en", "de", "fr", "ja" })
        {
            if (value.TryGetProperty(language, out var localized) && localized.ValueKind == JsonValueKind.String)
                return localized.GetString();
        }

        return null;
    }

    private static string? FindChannelPattern(JsonElement root)
    {
        if (TryGetObject(root, "channels", out var channels) || TryGetObject(root, "channel", out channels))
        {
            var channel = FindDirectString(channels, ["run_presence", "runPresence", "presence", "run", "runs"]);
            if (!string.IsNullOrWhiteSpace(channel))
                return channel;
        }

        return FindConfigString(root, ["channel_pattern", "channelPattern", "run_channel_pattern", "runChannelPattern"]);
    }

    private static string? FindEventName(JsonElement root, IReadOnlyList<string> names)
    {
        if (TryGetObject(root, "events", out var events))
        {
            var eventName = FindDirectString(events, names);
            if (!string.IsNullOrWhiteSpace(eventName))
                return eventName;
        }

        return FindConfigString(root, names);
    }

    private static string? FindConfigString(JsonElement root, IReadOnlyList<string> names)
    {
        var value = FindDirectString(root, names);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        foreach (var containerName in new[] { "reverb", "pusher", "connection", "websocket", "socket" })
        {
            if (TryGetObject(root, containerName, out var container))
            {
                value = FindDirectString(container, names);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string? FindDirectString(JsonElement root, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(root, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString();

                if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                    return value.ToString();
            }
        }

        return null;
    }

    private static int? FindConfigInt(JsonElement root, IReadOnlyList<string> names)
    {
        var value = FindDirectInt(root, names);
        if (value != null)
            return value;

        foreach (var containerName in new[] { "reverb", "pusher", "connection", "websocket", "socket" })
        {
            if (TryGetObject(root, containerName, out var container))
            {
                value = FindDirectInt(container, names);
                if (value != null)
                    return value;
            }
        }

        return null;
    }

    private static int? FindDirectInt(JsonElement root, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number;
        }

        return null;
    }

    private static bool? FindConfigBool(JsonElement root, IReadOnlyList<string> names)
    {
        var value = FindDirectBool(root, names);
        if (value != null)
            return value;

        foreach (var containerName in new[] { "reverb", "pusher", "connection", "websocket", "socket" })
        {
            if (TryGetObject(root, containerName, out var container))
            {
                value = FindDirectBool(container, names);
                if (value != null)
                    return value;
            }
        }

        return null;
    }

    private static bool? FindDirectBool(JsonElement root, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.True)
                return true;

            if (value.ValueKind == JsonValueKind.False)
                return false;

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var boolean))
                return boolean;
        }

        return null;
    }

    private static bool TryGetObject(JsonElement root, string propertyName, out JsonElement value)
    {
        if (TryGetProperty(root, propertyName, out value) && value.ValueKind == JsonValueKind.Object)
            return true;

        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record BroadcastAuthRequest(
        [property: JsonPropertyName("socket_id")] string SocketId,
        [property: JsonPropertyName("channel_name")] string ChannelName);

    private sealed class BroadcastAuthResponse
    {
        [JsonPropertyName("auth")]
        public string? Auth { get; set; }

        [JsonPropertyName("channel_data")]
        public JsonElement ChannelData { get; set; }
    }

    private sealed record RunCommandRequest(
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("target")] RunCommandTarget Target,
        [property: JsonPropertyName("payload")] object Payload,
        [property: JsonPropertyName("expires_in_seconds")] int ExpiresInSeconds,
        [property: JsonPropertyName("idempotency_key")] string? IdempotencyKey);

    private sealed record RunCommandTarget(
        [property: JsonPropertyName("type")] string Type);

    private sealed record RunCommandAckRequest(
        [property: JsonPropertyName("status")] string Status);

    private sealed record PartySnapshotRequest(
        [property: JsonPropertyName("seq")] int Sequence,
        [property: JsonPropertyName("party_key")] string PartyKey,
        [property: JsonPropertyName("members")] IReadOnlyList<PartySnapshotMemberRequest> Members);

    private sealed record PartySnapshotMemberRequest(
        [property: JsonPropertyName("p")] int Position,
        [property: JsonPropertyName("cid")] long? CharacterId,
        [property: JsonPropertyName("n")] string? Name,
        [property: JsonPropertyName("w")] string? World,
        [property: JsonPropertyName("cj")] int? ClassJobId,
        [property: JsonPropertyName("pj")] int? PhantomJobId);

    private sealed record RunCheckInsRequest(
        [property: JsonPropertyName("slot_ids")] IReadOnlyList<int> SlotIds,
        [property: JsonPropertyName("character_ids")] IReadOnlyList<long> CharacterIds);

    private sealed class GroupsResponse
    {
        [JsonPropertyName("data")]
        public List<GroupDto> Data { get; set; } = [];
    }

    private sealed class GroupRunsResponse
    {
        [JsonPropertyName("group")]
        public GroupSummaryDto? Group { get; set; }

        [JsonPropertyName("data")]
        public List<RunDto> Data { get; set; } = [];
    }

    private sealed class RunDetailResponse
    {
        [JsonPropertyName("data")]
        public RunDetailDto? Data { get; set; }
    }

    private sealed class ApplicationResponse
    {
        [JsonPropertyName("data")]
        public ApplicationDto? Data { get; set; }
    }

    private sealed class GroupDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("slug")]
        public string Slug { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("profile_picture_url")]
        public string? ProfilePictureUrl { get; set; }

        [JsonPropertyName("banner_image_url")]
        public string? BannerImageUrl { get; set; }

        [JsonPropertyName("datacenter")]
        public string? Datacenter { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("can_moderate")]
        public bool CanModerate { get; set; }
    }

    private sealed class GroupSummaryDto
    {
        [JsonPropertyName("can_moderate")]
        public bool CanModerate { get; set; }
    }

    private sealed class RunDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("group_id")]
        public int GroupId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("starts_at")]
        public DateTimeOffset StartsAt { get; set; }

        [JsonPropertyName("ends_at")]
        public DateTimeOffset? EndsAt { get; set; }

        [JsonPropertyName("duration_minutes")]
        public int? DurationMinutes { get; set; }

        [JsonPropertyName("datacenter")]
        public string? Datacenter { get; set; }

        [JsonPropertyName("is_public")]
        public bool IsPublic { get; set; }

        [JsonPropertyName("needs_application")]
        public bool NeedsApplication { get; set; }

        [JsonPropertyName("application_count")]
        public int? ApplicationCount { get; set; }

        [JsonPropertyName("activity_type")]
        public ActivityTypeDto? ActivityType { get; set; }
    }

    private sealed class ActivityTypeDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("difficulty")]
        public string? Difficulty { get; set; }

        [JsonPropertyName("small_image_url")]
        public string? SmallImageUrl { get; set; }

        [JsonPropertyName("banner_image_url")]
        public string? BannerImageUrl { get; set; }
    }

    private sealed class RunDetailDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("group_id")]
        public int GroupId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("starts_at")]
        public DateTimeOffset StartsAt { get; set; }

        [JsonPropertyName("duration_minutes")]
        public int? DurationMinutes { get; set; }

        [JsonPropertyName("application_count")]
        public int? ApplicationCount { get; set; }

        [JsonPropertyName("can_moderate")]
        public bool CanModerate { get; set; }

        [JsonPropertyName("roster")]
        public RosterDto? Roster { get; set; }
    }

    private sealed class RosterDto
    {
        [JsonPropertyName("slots")]
        public List<RosterSlotDto> Slots { get; set; } = [];
    }

    private sealed class RosterSlotDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("group_key")]
        public string GroupKey { get; set; } = string.Empty;

        [JsonPropertyName("group_label")]
        public LocalizedStringDto? GroupLabel { get; set; }

        [JsonPropertyName("slot_key")]
        public string SlotKey { get; set; } = string.Empty;

        [JsonPropertyName("slot_label")]
        public LocalizedStringDto? SlotLabel { get; set; }

        [JsonPropertyName("position_in_group")]
        public int? PositionInGroup { get; set; }

        [JsonPropertyName("sort_order")]
        public int? SortOrder { get; set; }

        [JsonPropertyName("is_host")]
        public bool IsHost { get; set; }

        [JsonPropertyName("is_raid_leader")]
        public bool IsRaidLeader { get; set; }

        [JsonPropertyName("assigned_character")]
        public RosterCharacterDto? AssignedCharacter { get; set; }

        [JsonPropertyName("field_values")]
        public List<FieldValueDto> FieldValues { get; set; } = [];

        [JsonPropertyName("assignment")]
        public AssignmentDto? Assignment { get; set; }
    }

    private sealed class RosterCharacterDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("world")]
        public string World { get; set; } = string.Empty;

        [JsonPropertyName("datacenter")]
        public string Datacenter { get; set; } = string.Empty;

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; }

        [JsonPropertyName("user")]
        public RosterUserDto? User { get; set; }
    }

    private sealed class RosterUserDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class FieldValueDto
    {
        [JsonPropertyName("field_key")]
        public string FieldKey { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public SlotFieldValueDto? Value { get; set; }
    }

    private sealed class SlotFieldValueDto
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("shorthand")]
        public string? Shorthand { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("max_level")]
        public int? MaxLevel { get; set; }

        [JsonPropertyName("label")]
        public LocalizedStringDto? Label { get; set; }

        [JsonPropertyName("icon_id")]
        public int? IconId { get; set; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("black_icon_url")]
        public string? BlackIconUrl { get; set; }

        [JsonPropertyName("transparent_icon_url")]
        public string? TransparentIconUrl { get; set; }

        [JsonPropertyName("sprite_url")]
        public string? SpriteUrl { get; set; }

        [JsonPropertyName("small_image_url")]
        public string? SmallImageUrl { get; set; }

        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; set; }
    }

    private sealed class AssignmentDto
    {
        [JsonPropertyName("application_id")]
        public int? ApplicationId { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("attendance_status")]
        public string? AttendanceStatus { get; set; }
    }

    private sealed class LocalizedStringDto
    {
        [JsonPropertyName("en")]
        public string? En { get; set; }
    }

    private sealed class ApplicationDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("activity_id")]
        public int ActivityId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("review_reason")]
        public string? ReviewReason { get; set; }

        [JsonPropertyName("submitted_at")]
        public DateTimeOffset SubmittedAt { get; set; }

        [JsonPropertyName("reviewed_at")]
        public DateTimeOffset? ReviewedAt { get; set; }

        [JsonPropertyName("user")]
        public ApplicationUserDto? User { get; set; }

        [JsonPropertyName("selected_character")]
        public ApplicationCharacterDto? SelectedCharacter { get; set; }

        [JsonPropertyName("answers")]
        public List<ApplicationAnswerDto> Answers { get; set; } = [];

        [JsonPropertyName("details")]
        public ApplicationDetailsDto? Details { get; set; }
    }

    private sealed class ApplicationUserDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; }
    }

    private sealed class ApplicationCharacterDto
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("lodestone_id")]
        public string? LodestoneId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("world")]
        public string World { get; set; } = string.Empty;

        [JsonPropertyName("datacenter")]
        public string Datacenter { get; set; } = string.Empty;

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; }

        [JsonPropertyName("is_claimed")]
        public bool? IsClaimed { get; set; }

        [JsonPropertyName("lodestone_refreshed_at")]
        public DateTimeOffset? LodestoneRefreshedAt { get; set; }

        [JsonPropertyName("lodestone_last_checked_at")]
        public DateTimeOffset? LodestoneLastCheckedAt { get; set; }

        [JsonPropertyName("occult_level")]
        public int? OccultLevel { get; set; }

        [JsonPropertyName("phantom_mastery")]
        public int? PhantomMastery { get; set; }

        [JsonPropertyName("blood_progress")]
        public ApplicationBloodProgressDto? BloodProgress { get; set; }
    }

    private sealed class ApplicationAnswerDto
    {
        [JsonPropertyName("question_key")]
        public string QuestionKey { get; set; } = string.Empty;

        [JsonPropertyName("question_label")]
        public LocalizedStringDto? QuestionLabel { get; set; }

        [JsonPropertyName("question_type")]
        public string QuestionType { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("value")]
        public JsonElement? Value { get; set; }

        [JsonPropertyName("raw_value")]
        public JsonElement? RawValue { get; set; }

        [JsonPropertyName("display_values")]
        public List<string> DisplayValues { get; set; } = [];

        [JsonPropertyName("role_values")]
        public List<string> RoleValues { get; set; } = [];

        [JsonPropertyName("display_items")]
        public List<ApplicationDisplayItemDto> DisplayItems { get; set; } = [];
    }

    private sealed class ApplicationDetailsDto
    {
        [JsonPropertyName("user")]
        public ApplicationUserDto? User { get; set; }

        [JsonPropertyName("applicant_character")]
        public ApplicationCharacterDto? ApplicantCharacter { get; set; }

        [JsonPropertyName("selected_character")]
        public ApplicationCharacterDto? SelectedCharacter { get; set; }

        [JsonPropertyName("answers")]
        public List<ApplicationAnswerDto> Answers { get; set; } = [];

        [JsonPropertyName("progress_milestones")]
        public List<ApplicationProgressMilestoneDto> ProgressMilestones { get; set; } = [];

        [JsonPropertyName("user_stats")]
        public ApplicationUserStatsDto? UserStats { get; set; }
    }

    private sealed class ApplicationBloodProgressDto
    {
        [JsonPropertyName("clears")]
        public int? Clears { get; set; }

        [JsonPropertyName("data_source")]
        public string? DataSource { get; set; }

        [JsonPropertyName("bosses")]
        public List<ApplicationBloodBossProgressDto> Bosses { get; set; } = [];
    }

    private sealed class ApplicationBloodBossProgressDto
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("kills")]
        public int? Kills { get; set; }

        [JsonPropertyName("progress")]
        public int? Progress { get; set; }

        [JsonPropertyName("progress_percent")]
        public int? ProgressPercent { get; set; }
    }

    private sealed class ApplicationProgressMilestoneDto
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public LocalizedStringDto? Label { get; set; }

        [JsonPropertyName("reached")]
        public bool Reached { get; set; }

        [JsonPropertyName("kills")]
        public int? Kills { get; set; }

        [JsonPropertyName("progress_percent")]
        public int? ProgressPercent { get; set; }
    }

    private sealed class ApplicationDisplayItemDto
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("flat_icon_url")]
        public string? FlatIconUrl { get; set; }

        [JsonPropertyName("transparent_icon_url")]
        public string? TransparentIconUrl { get; set; }
    }

    private sealed class ApplicationUserStatsDto
    {
        [JsonPropertyName("group_run_count")]
        public int? GroupRunCount { get; set; }

        [JsonPropertyName("overall_run_count")]
        public int? OverallRunCount { get; set; }

        [JsonPropertyName("class")]
        public ApplicationStatBucketDto? Class { get; set; }

        [JsonPropertyName("phantom_job")]
        public ApplicationStatBucketDto? PhantomJob { get; set; }
    }

    private sealed class ApplicationStatBucketDto
    {
        [JsonPropertyName("group")]
        public List<ApplicationStatItemDto> Group { get; set; } = [];

        [JsonPropertyName("overall")]
        public List<ApplicationStatItemDto> Overall { get; set; } = [];
    }

    private sealed class ApplicationStatItemDto
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("flat_icon_url")]
        public string? FlatIconUrl { get; set; }

        [JsonPropertyName("transparent_icon_url")]
        public string? TransparentIconUrl { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }
    }
}
