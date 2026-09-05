using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DiscordEventBot.Data;

public enum EventStatus
{
    Active = 0,
    Closed = 1
}

public enum PunishmentType
{
    Ban = 0,
    Kick = 1,
    TimedMute = 2,
    VoiceMicMute = 3
}

[Table("Events")]
public sealed class EventEntity
{
    [Key]
    public int Id { get; set; }

    public ulong GuildId { get; set; }
    public ulong HostUserId { get; set; }

    [MaxLength(64)]
    public string PresetId { get; set; } = "custom";

    [MaxLength(64)]
    public string CategoryKey { get; set; } = "custom";

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Emoji { get; set; } = "🎮";

    [MaxLength(512)]
    public string IconUrl { get; set; } = string.Empty;

    [MaxLength(16)]
    public string ColorHex { get; set; } = "#2B2D31";

    public int MaxParticipants { get; set; } = 10;

    [MaxLength(300)]
    public string InstructionLink { get; set; } = string.Empty;

    [MaxLength(100)]
    public string InstructionCode { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string InstructionText { get; set; } = string.Empty;

    public ulong CategoryChannelId { get; set; }
    public ulong ManageChannelId { get; set; }
    public ulong InfoChannelId { get; set; }
    public ulong ChatChannelId { get; set; }
    public ulong VoiceChannelId { get; set; }

    public ulong ManageMainMessageId { get; set; }
    public ulong ManageModMessageId { get; set; }
    public ulong InfoMessageId { get; set; }

    public bool IsVoiceClosed { get; set; }
    public bool IsAllMuted { get; set; }
    public bool IsAllDeafened { get; set; }

    public int ModerationPage { get; set; }
    public ulong? SelectedParticipantUserId { get; set; }

    public EventStatus Status { get; set; } = EventStatus.Active;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAtUtc { get; set; }

    [MaxLength(128)]
    public string? CloseReason { get; set; }

    public List<ParticipantEntity> Participants { get; set; } = new();
    public List<PunishmentEntity> Punishments { get; set; } = new();
    public List<WarningEntity> Warnings { get; set; } = new();
    public List<EventRoomEntity> AdditionalRooms { get; set; } = new();
}

[Table("EventRooms")]
public sealed class EventRoomEntity
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public EventEntity? Event { get; set; }

    public ulong VoiceChannelId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int MaxParticipants { get; set; } = 10;
    public bool IsVoiceClosed { get; set; }
    public bool IsAllMuted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

[Table("Participants")]
public sealed class ParticipantEntity
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public EventEntity? Event { get; set; }

    public ulong UserId { get; set; }
    public bool IsInVoice { get; set; }
    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LeftVoiceOrRegisteredAtUtc { get; set; } = DateTime.UtcNow;
}

[Table("Punishments")]
public sealed class PunishmentEntity
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public EventEntity? Event { get; set; }

    public ulong TargetUserId { get; set; }
    public ulong ModeratorUserId { get; set; }
    public PunishmentType Type { get; set; }

    [MaxLength(300)]
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? RevokedAtUtc { get; set; }
    public ulong? RevokedByUserId { get; set; }
}

[Table("Warnings")]
public sealed class WarningEntity
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public EventEntity? Event { get; set; }

    public ulong TargetUserId { get; set; }
    public ulong ModeratorUserId { get; set; }

    [MaxLength(300)]
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

[Table("ControlLog")]
public sealed class ControlLogEntity
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [MaxLength(100)]
    public string EventName { get; set; } = string.Empty;

    public ulong HostUserId { get; set; }
    public ulong ControllerUserId { get; set; }

    [MaxLength(250)]
    public string ActionDescription { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

[Table("EventHistory")]
public sealed class EventHistoryEntity
{
    [Key]
    public int Id { get; set; }

    public int OriginalEventId { get; set; }
    public ulong GuildId { get; set; }
    public ulong HostUserId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(64)]
    public string CategoryKey { get; set; } = string.Empty;

    public int TotalParticipantsCount { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime EndedAtUtc { get; set; }

    [MaxLength(64)]
    public string Status { get; set; } = "CLOSED";

    [MaxLength(128)]
    public string CloseReason { get; set; } = string.Empty;
}

[Table("CustomEventTemplates")]
public sealed class CustomEventTemplateEntity
{
    [Key]
    public int Id { get; set; }

    public ulong UserId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    public int MaxParticipants { get; set; } = 10;
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;
}
