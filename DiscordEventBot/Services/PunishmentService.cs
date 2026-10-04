using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using DiscordEventBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Services;

public sealed partial class PunishmentService
{
    public const int WarningsForAutoMute = 3;
    private static readonly TimeSpan AutoMuteDurationOnThreeWarnings = TimeSpan.FromHours(2);

    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly DiscordSocketClient _client;
    private readonly HierarchyService _hierarchyService;
    private readonly PermissionService _permissionService;
    private readonly LoggingService _loggingService;
    private readonly EmbedUpdateQueueService _embedQueue;
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;
    private readonly ILogger<PunishmentService> _logger;

    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), byte> _pendingVoiceUnmutes = new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), byte> _pendingVoiceUndeafens = new();

    [GeneratedRegex(@"^\s*(?<val>\d+)\s*(?<unit>[smhdсмчд]?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DurationRegex();

    public PunishmentService(
        IDbContextFactory<BotDbContext> dbFactory,
        DiscordSocketClient client,
        HierarchyService hierarchyService,
        PermissionService permissionService,
        LoggingService loggingService,
        EmbedUpdateQueueService embedQueue,
        IOptionsMonitor<BotSettings> settingsMonitor,
        ILogger<PunishmentService> logger)
    {
        _dbFactory = dbFactory;
        _client = client;
        _hierarchyService = hierarchyService;
        _permissionService = permissionService;
        _loggingService = loggingService;
        _embedQueue = embedQueue;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    private BotSettings Settings => _settingsMonitor.CurrentValue;

    public void RegisterPendingVoiceUnmute(ulong guildId, ulong userId)
    {
        _pendingVoiceUnmutes[(guildId, userId)] = 1;
    }

    public void ClearPendingVoiceUnmute(ulong guildId, ulong userId)
    {
        _pendingVoiceUnmutes.TryRemove((guildId, userId), out _);
    }

    public bool TryConsumePendingVoiceUnmute(ulong guildId, ulong userId)
    {
        return _pendingVoiceUnmutes.TryRemove((guildId, userId), out _);
    }

    public void RegisterPendingVoiceUndeafen(ulong guildId, ulong userId)
    {
        _pendingVoiceUndeafens[(guildId, userId)] = 1;
    }

    public void ClearPendingVoiceUndeafen(ulong guildId, ulong userId)
    {
        _pendingVoiceUndeafens.TryRemove((guildId, userId), out _);
    }

    public bool TryConsumePendingVoiceUndeafen(ulong guildId, ulong userId)
    {
        return _pendingVoiceUndeafens.TryRemove((guildId, userId), out _);
    }

    private static bool IsUserInAnyEventVoice(SocketGuildUser? user, EventEntity ev)
    {
        var chId = user?.VoiceChannel?.Id;
        if (!chId.HasValue)
            return false;

        return chId.Value == ev.VoiceChannelId || ev.AdditionalRooms.Any(r => r.VoiceChannelId == chId.Value);
    }

    private static bool IsRoomAllMuted(EventEntity ev, ulong voiceChannelId)
    {
        if (voiceChannelId == ev.VoiceChannelId)
            return ev.IsAllMuted;

        var subRoom = ev.AdditionalRooms.FirstOrDefault(r => r.VoiceChannelId == voiceChannelId);
        return subRoom?.IsAllMuted ?? false;
    }

    public static bool TryParseDuration(string? rawInput, out TimeSpan duration, out string errorMessage)
    {
        duration = TimeSpan.Zero;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            errorMessage = "Укажите длительность (например: `30m`, `2h`, `1d`, `7d`).";
            return false;
        }

        var match = DurationRegex().Match(rawInput);
        if (!match.Success || !int.TryParse(match.Groups["val"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            errorMessage = "Некорректный формат длительности. Примеры: `30m`, `2h`, `1d`, `7d` (минимум `1m`).";
            return false;
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        duration = unit switch
        {
            "s" or "с" => TimeSpan.FromSeconds(amount),
            "m" or "м" or "" => TimeSpan.FromMinutes(amount),
            "h" or "ч" => TimeSpan.FromHours(amount),
            "d" or "д" => TimeSpan.FromDays(amount),
            _ => TimeSpan.FromMinutes(amount)
        };

        if (duration < TimeSpan.FromMinutes(1))
        {
            errorMessage = "Минимальная длительность — 1 минута (`1m`).";
            return false;
        }

        if (duration > TimeSpan.FromDays(365))
        {
            errorMessage = "Максимальная длительность — 365 дней (`365d`).";
            return false;
        }

        return true;
    }

    public async Task<bool> HasActiveBanAsync(int eventId, ulong targetUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Punishments.AnyAsync(p =>
            p.EventId == eventId &&
            p.TargetUserId == targetUserId &&
            p.Type == PunishmentType.Ban &&
            p.IsActive);
    }

    public async Task<(bool Success, string Message, bool NowBanned)> ToggleBanAsync(
        int eventId,
        IGuildUser actor,
        ulong targetUserId,
        string? reason = null,
        TimeSpan? duration = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Punishments)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", false);

        var activeBan = ev.Punishments.FirstOrDefault(p =>
            p.TargetUserId == targetUserId && p.Type == PunishmentType.Ban && p.IsActive);

        if (activeBan is not null)
        {
            var res = await UnbanUserAsync(eventId, actor, targetUserId);
            return (res.Success, res.Message, false);
        }
        else
        {
            var res = await BanUserAsync(eventId, actor, targetUserId, reason, duration);
            return (res.Success, res.Message, res.Success);
        }
    }

    public async Task<(bool Success, string Message)> BanUserAsync(
        int eventId,
        IGuildUser actor,
        ulong targetUserId,
        string? reason = null,
        TimeSpan? duration = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "У вас нет прав для управления этим ивентом.");

        if (!_hierarchyService.CanPunish(guild, actor, targetUserId, ev.HostUserId, _client.CurrentUser.Id, out var err))
            return (false, err);

        var existingBan = ev.Punishments.FirstOrDefault(p =>
            p.TargetUserId == targetUserId && p.Type == PunishmentType.Ban && p.IsActive);

        if (existingBan is not null)
            return (false, $"Пользователь <@{targetUserId}> уже забанен в этом ивенте.");

        var cleanReason = string.IsNullOrWhiteSpace(reason) ? "Без указания причины" : reason.Trim();
        var expiresAt = duration.HasValue ? DateTime.UtcNow.Add(duration.Value) : (DateTime?)null;

        db.Punishments.Add(new PunishmentEntity
        {
            EventId = ev.Id,
            TargetUserId = targetUserId,
            ModeratorUserId = actor.Id,
            Type = PunishmentType.Ban,
            Reason = cleanReason,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAt,
            IsActive = true
        });

        var participant = ev.Participants.FirstOrDefault(p => p.UserId == targetUserId);
        if (participant is not null)
        {
            db.Participants.Remove(participant);
        }

        if (ev.SelectedParticipantUserId == targetUserId)
        {
            ev.SelectedParticipantUserId = null;
        }

        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = $"Бан участника <@{targetUserId}> (причина: {cleanReason})",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        var targetGuildUser = guild.GetUser(targetUserId);
        var banRoleId = Settings.Event.Roles.EventBan;
        if (targetGuildUser is not null)
        {
            if (banRoleId != 0 && guild.GetRole(banRoleId) is SocketRole banRole && !targetGuildUser.Roles.Any(r => r.Id == banRoleId))
            {
                try
                {
                    await targetGuildUser.AddRoleAsync(banRole);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось выдать роль EventBan пользователю {UserId}.", targetUserId);
                }
            }

            if (IsUserInAnyEventVoice(targetGuildUser, ev))
            {
                try
                {
                    await targetGuildUser.ModifyAsync(x => x.Channel = null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось отключить забаненного пользователя {UserId} от голосового канала.", targetUserId);
                }
            }

            await _permissionService.RevokeParticipantClosedAccessAsync(
                guild,
                ev.VoiceChannelId,
                ev.ChatChannelId,
                targetUserId);

            foreach (var room in ev.AdditionalRooms)
            {
                if (guild.GetVoiceChannel(room.VoiceChannelId) is SocketVoiceChannel subVc)
                {
                    try { await subVc.RemovePermissionOverwriteAsync(targetGuildUser); } catch { }
                }
            }
        }

        var expiryText = expiresAt.HasValue
            ? $"до <t:{new DateTimeOffset(DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds()}:F> (<t:{new DateTimeOffset(DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds()}:R>)"
            : "на время ивента";

        await _loggingService.LogEventAsync(
            LogCategory.Punishments,
            "🚫 Бан на ивентах",
            $"Пользователь <@{targetUserId}> забанен ({expiryText}) в ивенте **{ev.Name}**.\nПричина: `{cleanReason}`",
            actorUserId: actor.Id,
            targetUserId: targetUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        if (actor.Id != ev.HostUserId)
        {
            await _loggingService.LogEventAsync(
                LogCategory.Control,
                "🛡 Контроль: Бан участника",
                $"Контролёр <@{actor.Id}> забанил <@{targetUserId}> ({expiryText}) в чужом ивенте **{ev.Name}** (ведущий <@{ev.HostUserId}>).",
                actorUserId: actor.Id,
                targetUserId: targetUserId,
                whereInfo: $"Ивент #{ev.Id} ({ev.Name})");
        }

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Пользователь <@{targetUserId}> забанен {expiryText} (причина: {cleanReason}).");
    }

    public async Task RemoveExpiredBanAsync(int punishmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ban = await db.Punishments
            .Include(p => p.Event)
            .FirstOrDefaultAsync(p => p.Id == punishmentId && p.Type == PunishmentType.Ban && p.IsActive);

        if (ban is null)
            return;

        ban.IsActive = false;
        ban.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var guildId = ban.Event?.GuildId ?? Settings.GuildId;
        var guild = _client.GetGuild(guildId);
        if (guild is not null)
        {
            await TryRemoveEventBanRoleIfNoActiveBansAsync(db, guild, ban.TargetUserId);
        }

        await _loggingService.LogEventAsync(
            LogCategory.Punishments,
            "✅ Автоматический разбан (истёк срок)",
            $"С пользователя <@{ban.TargetUserId}> автоматически снята роль `EventBan` по истечении срока бана.",
            targetUserId: ban.TargetUserId,
            whereInfo: ban.Event is not null ? $"Ивент #{ban.Event.Id} ({ban.Event.Name})" : "Сервер",
            color: Color.Green);

        if (ban.Event is not null && ban.Event.Status == EventStatus.Active)
        {
            await _embedQueue.RefreshEventMessagesNowAsync(ban.Event.Id);
        }
    }

    public async Task<(bool Success, string Message)> UnbanUserAsync(
        int eventId,
        IGuildUser actor,
        ulong targetUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Punishments)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (ev is null)
            return (false, "Ивент не найден.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "У вас нет прав для управления этим ивентом.");

        var activeBans = await db.Punishments
            .Where(p => p.TargetUserId == targetUserId && p.Type == PunishmentType.Ban && p.IsActive)
            .ToListAsync();

        if (activeBans.Count == 0)
        {
            await TryRemoveEventBanRoleIfNoActiveBansAsync(db, guild, targetUserId);
            return (true, $"С пользователя <@{targetUserId}> снят бан и роль EventBan.");
        }

        foreach (var ban in activeBans)
        {
            ban.IsActive = false;
            ban.RevokedAtUtc = DateTime.UtcNow;
            ban.RevokedByUserId = actor.Id;
        }

        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = $"Разбан участника <@{targetUserId}>",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        await TryRemoveEventBanRoleIfNoActiveBansAsync(db, guild, targetUserId);

        await _loggingService.LogEventAsync(
            LogCategory.Punishments,
            "✅ Разбан в ивенте",
            $"С пользователя <@{targetUserId}> снят бан в ивенте **{ev.Name}**.",
            actorUserId: actor.Id,
            targetUserId: targetUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})",
            color: Color.Green);

        if (actor.Id != ev.HostUserId)
        {
            await _loggingService.LogEventAsync(
                LogCategory.Control,
                "🛡 Контроль: Разбан участника",
                $"Контролёр <@{actor.Id}> разбанил <@{targetUserId}> в ивенте **{ev.Name}** (ведущий <@{ev.HostUserId}>).",
                actorUserId: actor.Id,
                targetUserId: targetUserId,
                whereInfo: $"Ивент #{ev.Id} ({ev.Name})");
        }

        if (ev.Status == EventStatus.Active)
        {
            await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        }

        return (true, $"Пользователь <@{targetUserId}> разбанен.");
    }

    public async Task TryRemoveEventBanRoleIfNoActiveBansAsync(BotDbContext db, SocketGuild guild, ulong targetUserId)
    {
        var hasAnyOtherActiveBan = await db.Punishments.AnyAsync(p =>
            p.TargetUserId == targetUserId &&
            p.Type == PunishmentType.Ban &&
            p.IsActive);

        if (hasAnyOtherActiveBan)
            return;

        var banRoleId = Settings.Event.Roles.EventBan;
        if (banRoleId == 0)
            return;

        var targetGuildUser = guild.GetUser(targetUserId);
        var banRole = guild.GetRole(banRoleId);
        if (targetGuildUser is not null && banRole is not null && targetGuildUser.Roles.Any(r => r.Id == banRoleId))
        {
            try
            {
                await targetGuildUser.RemoveRoleAsync(banRole);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось снять роль EventBan с пользователя {UserId}.", targetUserId);
            }
        }
    }

    public async Task<(bool Success, string Message)> KickUserAsync(
        int eventId,
        IGuildUser actor,
        ulong targetUserId,
        string? reason = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "У вас нет прав для управления этим ивентом.");

        if (!_hierarchyService.CanPunish(guild, actor, targetUserId, ev.HostUserId, _client.CurrentUser.Id, out var err))
            return (false, err);

        var participant = ev.Participants.FirstOrDefault(p => p.UserId == targetUserId);
        if (participant is not null)
        {
            db.Participants.Remove(participant);
        }

        if (ev.SelectedParticipantUserId == targetUserId)
        {
            ev.SelectedParticipantUserId = null;
        }

        var cleanReason = string.IsNullOrWhiteSpace(reason) ? "Кик ведущим/контролёром" : reason.Trim();

        db.Punishments.Add(new PunishmentEntity
        {
            EventId = ev.Id,
            TargetUserId = targetUserId,
            ModeratorUserId = actor.Id,
            Type = PunishmentType.Kick,
            Reason = cleanReason,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = false
        });

        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = $"Кик участника <@{targetUserId}> ({cleanReason})",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        var targetGuildUser = guild.GetUser(targetUserId);
        if (IsUserInAnyEventVoice(targetGuildUser, ev) && targetGuildUser is not null)
        {
            try
            {
                await targetGuildUser.ModifyAsync(x => x.Channel = null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось отключить кикнутого пользователя {UserId} от голосового канала.", targetUserId);
            }
        }

        if (ev.IsVoiceClosed)
        {
            var hasActiveChatMute = ev.Punishments.Any(p =>
                p.TargetUserId == targetUserId && p.Type == PunishmentType.TimedMute && p.IsActive);

            await _permissionService.RevokeParticipantClosedAccessAsync(
                guild,
                ev.VoiceChannelId,
                ev.ChatChannelId,
                targetUserId,
                keepChatMuteOverwrite: hasActiveChatMute);
        }

        await _loggingService.LogEventAsync(
            LogCategory.Punishments,
            "👢 Кик из ивента",
            $"Пользователь <@{targetUserId}> кикнут из ивента **{ev.Name}** ({cleanReason}).",
            actorUserId: actor.Id,
            targetUserId: targetUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        if (actor.Id != ev.HostUserId)
        {
            await _loggingService.LogEventAsync(
                LogCategory.Control,
                "🛡 Контроль: Кик участника",
                $"Контролёр <@{actor.Id}> кикнул <@{targetUserId}> из ивента **{ev.Name}** (ведущий <@{ev.HostUserId}>).",
                actorUserId: actor.Id,
                targetUserId: targetUserId,
                whereInfo: $"Ивент #{ev.Id} ({ev.Name})");
        }

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Пользователь <@{targetUserId}> кикнут из ивента **{ev.Name}**.");
    }

    public async Task<bool> HasActiveTimedMuteAsync(int eventId, ulong targetUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Punishments.AnyAsync(p =>
            p.EventId == eventId &&
            p.TargetUserId == targetUserId &&
            p.Type == PunishmentType.TimedMute &&
            p.IsActive);
    }

    public async Task<(bool Success, string Message)> ApplyTimedMuteAsync(
        int eventId,
        IGuildUser actor,
        ulong targetUserId,
        TimeSpan duration,
        string? reason = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "У вас нет прав для управления этим ивентом.");

        if (!_hierarchyService.CanPunish(guild, actor, targetUserId, ev.HostUserId, _client.CurrentUser.Id, out var err))
            return (false, err);

        var existingMutes = ev.Punishments
            .Where(p => p.TargetUserId == targetUserId && p.Type == PunishmentType.TimedMute && p.IsActive)
            .ToList();

        foreach (var m in existingMutes)
        {
            m.IsActive = false;
            m.RevokedAtUtc = DateTime.UtcNow;
            m.RevokedByUserId = actor.Id;
        }

        var expiresAt = DateTime.UtcNow.Add(duration);
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? $"Мут на {(int)duration.TotalMinutes} мин." : reason.Trim();

        db.Punishments.Add(new PunishmentEntity
        {
            EventId = ev.Id,
            TargetUserId = targetUserId,
            ModeratorUserId = actor.Id,
            Type = PunishmentType.TimedMute,
            Reason = cleanReason,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAt,
            IsActive = true
        });

        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        ClearPendingVoiceUnmute(ev.GuildId, targetUserId);
        var isParticipant = ev.Participants.Any(p => p.UserId == targetUserId);
        await _permissionService.SetUserChatMuteOverwriteAsync(
            guild,
            ev.ChatChannelId,
            targetUserId,
            muted: true,
            isRoomClosed: ev.IsVoiceClosed,
            isParticipant: isParticipant);

        var targetUser = guild.GetUser(targetUserId);
        if (IsUserInAnyEventVoice(targetUser, ev) && targetUser is not null)
        {
            try
            {
                await targetUser.ModifyAsync(x => x.Mute = true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось выдать серверный мут в голосовом пользователю {UserId}.", targetUserId);
            }
        }

        var unixExpire = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
        await _loggingService.LogEventAsync(
            LogCategory.Punishments,
            "🔇 Временный мут в ивенте",
            $"Пользователю <@{targetUserId}> выдан мут в чате и голосовом канале до <t:{unixExpire}:F> (<t:{unixExpire}:R>).\nПричина: `{cleanReason}`",
            actorUserId: actor.Id,
            targetUserId: targetUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Пользователю <@{targetUserId}> выдан мут до <t:{unixExpire}:t> (<t:{unixExpire}:R>).");
    }

    public async Task<(bool Success, string Message)> RemoveTimedMuteAsync(
        int eventId,
        ulong? actorUserId,
        ulong targetUserId,
        bool isAutoExpired = false)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (ev is null)
            return (false, "Ивент не найден.");

        var activeMutes = ev.Punishments
            .Where(p => p.TargetUserId == targetUserId && p.Type == PunishmentType.TimedMute && p.IsActive)
            .ToList();

        if (activeMutes.Count == 0)
            return (false, "У участника нет активного временного мута.");

        foreach (var m in activeMutes)
        {
            m.IsActive = false;
            m.RevokedAtUtc = DateTime.UtcNow;
            m.RevokedByUserId = actorUserId;
        }

        await db.SaveChangesAsync();

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is not null && ev.Status == EventStatus.Active)
        {
            var isParticipant = ev.Participants.Any(p => p.UserId == targetUserId);
            await _permissionService.SetUserChatMuteOverwriteAsync(
                guild,
                ev.ChatChannelId,
                targetUserId,
                muted: false,
                isRoomClosed: ev.IsVoiceClosed,
                isParticipant: isParticipant);

            var hasMicMute = ev.Punishments.Any(p =>
                p.TargetUserId == targetUserId && p.Type == PunishmentType.VoiceMicMute && p.IsActive);

            if (!hasMicMute)
            {
                var targetUser = guild.GetUser(targetUserId);
                if (targetUser?.VoiceChannel is not null)
                {
                    if (!IsRoomAllMuted(ev, targetUser.VoiceChannel.Id))
                    {
                        try
                        {
                            await targetUser.ModifyAsync(x => x.Mute = false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Не удалось снять серверный мут с пользователя {UserId}.", targetUserId);
                        }
                    }
                }
                else
                {
                    RegisterPendingVoiceUnmute(ev.GuildId, targetUserId);
                }
            }
        }
        else
        {
            RegisterPendingVoiceUnmute(ev.GuildId, targetUserId);
        }

        await _loggingService.LogEventAsync(
            LogCategory.Punishments,
            "🔊 Размут в ивенте",
            isAutoExpired
                ? $"С пользователя <@{targetUserId}> автоматически снят временный мут по истечении срока."
                : $"С пользователя <@{targetUserId}> снят временный мут.",
            actorUserId: actorUserId,
            targetUserId: targetUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})",
            color: Color.Green);

        if (ev.Status == EventStatus.Active)
        {
            await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        }

        return (true, $"С пользователя <@{targetUserId}> снят временный мут.");
    }

    public async Task<(bool Success, string Message, bool IsNowMuted)> ToggleVoiceMicMuteAsync(
        int eventId,
        IGuildUser actor,
        ulong targetUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Punishments)
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", false);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", false);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "У вас нет прав для управления этим ивентом.", false);

        var existingMicMute = ev.Punishments.FirstOrDefault(p =>
            p.TargetUserId == targetUserId && p.Type == PunishmentType.VoiceMicMute && p.IsActive);

        if (existingMicMute is null)
        {
            if (!_hierarchyService.CanPunish(guild, actor, targetUserId, ev.HostUserId, _client.CurrentUser.Id, out var err))
                return (false, err, false);

            db.Punishments.Add(new PunishmentEntity
            {
                EventId = ev.Id,
                TargetUserId = targetUserId,
                ModeratorUserId = actor.Id,
                Type = PunishmentType.VoiceMicMute,
                Reason = "Выкл. микрофон в голосовом канале",
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true
            });

            ev.LastActivityAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

            ClearPendingVoiceUnmute(ev.GuildId, targetUserId);

            var targetUser = guild.GetUser(targetUserId);
            if (IsUserInAnyEventVoice(targetUser, ev) && targetUser is not null)
            {
                try
                {
                    await targetUser.ModifyAsync(x => x.Mute = true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось выключить микрофон пользователю {UserId}.", targetUserId);
                }
            }

            await _loggingService.LogEventAsync(
                LogCategory.Punishments,
                "🎙 Выключен микрофон",
                $"Пользователю <@{targetUserId}> выключен микрофон (серверный mute) в ивенте **{ev.Name}**.",
                actorUserId: actor.Id,
                targetUserId: targetUserId,
                whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

            await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
            return (true, $"Микрофон пользователя <@{targetUserId}> выключен.", true);
        }
        else
        {
            existingMicMute.IsActive = false;
            existingMicMute.RevokedAtUtc = DateTime.UtcNow;
            existingMicMute.RevokedByUserId = actor.Id;
            ev.LastActivityAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var hasTimedMute = ev.Punishments.Any(p =>
                p.TargetUserId == targetUserId && p.Type == PunishmentType.TimedMute && p.IsActive);

            if (!hasTimedMute)
            {
                var targetUser = guild.GetUser(targetUserId);
                if (targetUser?.VoiceChannel is not null)
                {
                    if (!IsRoomAllMuted(ev, targetUser.VoiceChannel.Id))
                    {
                        try
                        {
                            await targetUser.ModifyAsync(x => x.Mute = false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Не удалось включить микрофон пользователю {UserId}.", targetUserId);
                        }
                    }
                }
                else
                {
                    RegisterPendingVoiceUnmute(ev.GuildId, targetUserId);
                }
            }

            await _loggingService.LogEventAsync(
                LogCategory.Punishments,
                "🎙 Включён микрофон",
                $"Пользователю <@{targetUserId}> включён микрофон в ивенте **{ev.Name}**.",
                actorUserId: actor.Id,
                targetUserId: targetUserId,
                whereInfo: $"Ивент #{ev.Id} ({ev.Name})",
                color: Color.Green);

            await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
            return (true, $"Микрофон пользователя <@{targetUserId}> включён.", false);
        }
    }

    public async Task<(bool Success, string Message)> AddWarningAsync(
        int eventId,
        IGuildUser actor,
        ulong targetUserId,
        string? reason = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Warnings)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "У вас нет прав для управления этим ивентом.");

        if (!_hierarchyService.CanPunish(guild, actor, targetUserId, ev.HostUserId, _client.CurrentUser.Id, out var err))
            return (false, err);

        var cleanReason = string.IsNullOrWhiteSpace(reason) ? "Предупреждение ведущим" : reason.Trim();

        db.Warnings.Add(new WarningEntity
        {
            EventId = ev.Id,
            TargetUserId = targetUserId,
            ModeratorUserId = actor.Id,
            Reason = cleanReason,
            CreatedAtUtc = DateTime.UtcNow
        });

        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var userWarnings = await db.Warnings
            .Where(w => w.EventId == ev.Id && w.TargetUserId == targetUserId)
            .ToListAsync();

        var totalWarnings = userWarnings.Count;

        await _loggingService.LogEventAsync(
            LogCategory.Punishments,
            "⚠️ Предупреждение участнику",
            $"Участник <@{targetUserId}> получил предупреждение (**{totalWarnings}/{WarningsForAutoMute}**) в ивенте **{ev.Name}**.",
            actorUserId: actor.Id,
            targetUserId: targetUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        if (totalWarnings >= WarningsForAutoMute)
        {
            db.Warnings.RemoveRange(userWarnings);
            await db.SaveChangesAsync();

            await ApplyTimedMuteAsync(
                eventId,
                actor,
                targetUserId,
                AutoMuteDurationOnThreeWarnings,
                "Автомут на 2 часа (достигнуто 3 предупреждения)");

            return (true, $"Участник <@{targetUserId}> получил 3-е предупреждение: выдан автоматический мут на **2 часа**, все предупреждения сняты.");
        }

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Участнику <@{targetUserId}> выдано предупреждение (**{totalWarnings}/{WarningsForAutoMute}**).");
    }
}
