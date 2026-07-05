using System;
using System.Collections.Generic;

namespace FullParty.Models;

public sealed record FullPartyGroup(
    int Id,
    string Slug,
    string Name,
    string? ProfilePictureUrl,
    string? BannerImageUrl,
    string? Datacenter,
    string Role,
    bool CanModerate);

public sealed record FullPartyRun(
    int Id,
    int GroupId,
    string Status,
    string Name,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    int? DurationMinutes,
    string? Datacenter,
    bool IsPublic,
    bool NeedsApplication,
    int? ApplicationCount,
    FullPartyActivity? ActivityType,
    bool? CanModerate = null);

public sealed record FullPartyActivity(
    int Id,
    string DisplayName,
    string? Difficulty,
    string? SmallImageUrl,
    string? BannerImageUrl);

public sealed record FullPartyRunDetail(
    int Id,
    int GroupId,
    string Status,
    DateTimeOffset StartsAt,
    int? DurationMinutes,
    int? ApplicationCount,
    bool CanModerate,
    IReadOnlyList<FullPartyRosterSlot> Slots);

public sealed record FullPartyRosterSlot(
    int Id,
    string GroupKey,
    string GroupLabel,
    string SlotKey,
    string SlotLabel,
    int? PositionInGroup,
    int? SortOrder,
    bool IsHost,
    bool IsRaidLeader,
    int? ApplicationId,
    string? AssignmentSource,
    string? AttendanceStatus,
    FullPartyRosterCharacter? AssignedCharacter,
    int? CharacterClassId,
    string? CharacterClass,
    string? CharacterClassRole,
    int? PhantomJobId,
    string? PhantomJob,
    int? PhantomJobMaxLevel,
    int? PhantomJobIconId,
    string? PhantomJobIconUrl,
    IReadOnlyList<string> PhantomJobIconUrls);

public sealed record FullPartyRosterCharacter(
    long Id,
    string Name,
    string World,
    string Datacenter,
    string? AvatarUrl,
    string? UserName);

public sealed record FullPartyApplication(
    int Id,
    int ActivityId,
    string Status,
    string? Notes,
    string? ReviewReason,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string UserName,
    string? UserAvatarUrl,
    FullPartyRosterCharacter? SelectedCharacter,
    FullPartyApplicationDetails? Details,
    IReadOnlyList<FullPartyApplicationAnswer> Answers);

public sealed record FullPartyApplicationAnswer(
    string QuestionKey,
    string QuestionLabel,
    string QuestionType,
    string? Value);

public sealed record FullPartyApplicationDetails(
    FullPartyApplicationUser? User,
    FullPartyApplicationCharacter? ApplicantCharacter,
    FullPartyApplicationCharacter? SelectedCharacter,
    IReadOnlyList<FullPartyApplicationDetailedAnswer> Answers,
    IReadOnlyList<FullPartyProgressMilestone> ProgressMilestones,
    FullPartyApplicationUserStats? UserStats);

public sealed record FullPartyApplicationUser(
    long Id,
    string Name,
    string? AvatarUrl);

public sealed record FullPartyApplicationCharacter(
    long? Id,
    string? LodestoneId,
    string Name,
    string World,
    string Datacenter,
    string? AvatarUrl,
    bool? IsClaimed,
    DateTimeOffset? LodestoneLastCheckedAt,
    int? OccultLevel,
    int? PhantomMastery,
    FullPartyBloodProgress? BloodProgress);

public sealed record FullPartyBloodProgress(
    int? Clears,
    string? DataSource,
    IReadOnlyList<FullPartyBloodBossProgress> Bosses);

public sealed record FullPartyBloodBossProgress(
    string Key,
    int? Kills,
    int? ProgressPercent);

public sealed record FullPartyProgressMilestone(
    string Key,
    string Label,
    bool Reached,
    int? Kills,
    int? ProgressPercent);

public sealed record FullPartyApplicationDetailedAnswer(
    string QuestionKey,
    string QuestionLabel,
    string QuestionType,
    string? Source,
    IReadOnlyList<string> DisplayValues,
    IReadOnlyList<string> RoleValues,
    IReadOnlyList<FullPartyApplicationDisplayItem> DisplayItems);

public sealed record FullPartyApplicationDisplayItem(
    string Label,
    string? Role,
    string? IconUrl,
    string? FlatIconUrl,
    string? TransparentIconUrl);

public sealed record FullPartyApplicationUserStats(
    int? GroupRunCount,
    int? OverallRunCount,
    FullPartyApplicationStatBucket? Class,
    FullPartyApplicationStatBucket? PhantomJob);

public sealed record FullPartyApplicationStatBucket(
    IReadOnlyList<FullPartyApplicationStatItem> Group,
    IReadOnlyList<FullPartyApplicationStatItem> Overall);

public sealed record FullPartyApplicationStatItem(
    string Key,
    string Label,
    string? Role,
    string? IconUrl,
    string? FlatIconUrl,
    string? TransparentIconUrl,
    int Count);
