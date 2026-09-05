namespace DiscordEventBot.Configuration;

public sealed class BotSettings
{
    public const ulong HardcodedDeveloperUserId = 1059057265593823292UL;

    public string Token { get; set; } = string.Empty;
    public ulong GuildId { get; set; } = 1060286255629344908UL;
    public string DatabasePath { get; set; } = "events.db";
    public EventConfiguration Event { get; set; } = new();
}

public sealed class EventConfiguration
{
    public ulong AnchorCategoryId { get; set; } = 1463206062424461547UL;
    public ulong LogChannelId { get; set; } = 1568748571770228917UL;
    public RoleSettings Roles { get; set; } = new();
    public List<ulong> DeveloperUserIds { get; set; } = new();
    public LimitSettings Limits { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public AntiAbuseSettings AntiAbuse { get; set; } = new();
    public List<EventPreset> Presets { get; set; } = new();
}

public sealed class RoleSettings
{
    public ulong Male { get; set; } = 1463204419075047606UL;
    public ulong Female { get; set; } = 1463204426268540929UL;
    public ulong EventMod { get; set; } = 1538176463835103323UL;
    public ulong Master { get; set; } = 1538176463835103323UL;
    public ulong Curator { get; set; } = 1538176463835103323UL;
    public ulong Admingroup { get; set; } = 1538176463835103323UL;
    public ulong Mentor { get; set; } = 1538176463835103323UL;
    public ulong StaffAdmin { get; set; } = 1538176463835103323UL;
    public ulong Developer { get; set; } = 1463204361583722527UL;
    public ulong EventBan { get; set; } = 1463204461441843283UL;
    public ulong Ping { get; set; } = 1568749136960819210UL;
}

public sealed class LimitSettings
{
    public int MaxActivePerUser { get; set; } = 1;
    public int MaxActiveTotal { get; set; } = 20;
    public int MinParticipants { get; set; } = 4;
    public int MaxParticipants { get; set; } = 99;
    public int DefaultParticipants { get; set; } = 10;
    public int AutoCloseHours { get; set; } = 12;
    public int RetentionDays { get; set; } = 30;
    public int WarningsBeforeKick { get; set; } = 3;
    public bool RemoveBanRoleOnEventEnd { get; set; } = false;
}

public sealed class LoggingSettings
{
    public bool Enabled { get; set; } = true;
    public bool Errors { get; set; } = true;
    public bool OrganizerActions { get; set; } = true;
    public bool EventCreateDelete { get; set; } = true;
    public bool JoinLeave { get; set; } = true;
    public bool Close { get; set; } = true;
    public bool Pings { get; set; } = true;
    public bool Punishments { get; set; } = true;
    public bool Control { get; set; } = true;
}

public sealed class AntiAbuseSettings
{
    public List<string> BannedWords { get; set; } = new();
    public bool BlockInviteLinks { get; set; } = true;
}

public sealed class EventPreset
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Emoji { get; set; } = "🎮";
    public string IconUrl { get; set; } = string.Empty;
    public string Color { get; set; } = "#3498DB";
    public int MaxParticipants { get; set; } = 10;
    public string Description { get; set; } = string.Empty;
    public ulong PingRoleId { get; set; } = 1568749136960819210UL;
}
