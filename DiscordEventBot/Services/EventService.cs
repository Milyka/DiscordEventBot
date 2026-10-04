using System.Collections.Concurrent;
using System.Globalization;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using DiscordEventBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Services;

public sealed class EventService
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly DiscordSocketClient _client;
    private readonly HierarchyService _hierarchyService;
    private readonly PermissionService _permissionService;
    private readonly AntiAbuseService _antiAbuseService;
    private readonly EmbedAndComponentBuilderService _embedBuilder;
    private readonly EmbedUpdateQueueService _embedQueue;
    private readonly PunishmentService _punishmentService;
    private readonly LoggingService _loggingService;
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;
    private readonly ILogger<EventService> _logger;

    private readonly SemaphoreSlim _createCloseLock = new(1, 1);
    private readonly ConcurrentDictionary<(int EventId, ulong UserId), byte> _pendingJoinRequests = new();

    public EventService(
        IDbContextFactory<BotDbContext> dbFactory,
        DiscordSocketClient client,
        HierarchyService hierarchyService,
        PermissionService permissionService,
        AntiAbuseService antiAbuseService,
        EmbedAndComponentBuilderService embedBuilder,
        EmbedUpdateQueueService embedQueue,
        PunishmentService punishmentService,
        LoggingService loggingService,
        IOptionsMonitor<BotSettings> settingsMonitor,
        ILogger<EventService> logger)
    {
        _dbFactory = dbFactory;
        _client = client;
        _hierarchyService = hierarchyService;
        _permissionService = permissionService;
        _antiAbuseService = antiAbuseService;
        _embedBuilder = embedBuilder;
        _embedQueue = embedQueue;
        _punishmentService = punishmentService;
        _loggingService = loggingService;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    public BotSettings Settings => _settingsMonitor.CurrentValue;

    public void ClearPendingJoinRequest(int eventId, ulong userId)
    {
        _pendingJoinRequests.TryRemove((eventId, userId), out _);
    }

    public async Task<(bool CanCreate, string ErrorMessage)> ValidateBeforeCreateAsync(
        ulong guildId,
        ulong hostUserId,
        string name,
        string? description,
        int maxParticipants)
    {
        var limits = Settings.Event.Limits;

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length is < 1 or > 100)
            return (false, "Название ивента должно содержать от 1 до 100 символов.");

        if (!string.IsNullOrEmpty(description) && description.Length > 500)
            return (false, "Описание ивента не может превышать 500 символов.");

        if (maxParticipants < limits.MinParticipants || maxParticipants > limits.MaxParticipants)
            return (false, $"Количество участников должно быть от {limits.MinParticipants} до {limits.MaxParticipants}.");

        if (!_antiAbuseService.ValidateText(name, description, out var abuseError))
            return (false, abuseError);

        await using var db = await _dbFactory.CreateDbContextAsync();

        var userActiveCount = await db.Events.CountAsync(e =>
            e.GuildId == guildId &&
            e.HostUserId == hostUserId &&
            e.Status == EventStatus.Active);

        if (userActiveCount >= limits.MaxActivePerUser)
            return (false, $"У вас уже есть активный ивент (лимит: {limits.MaxActivePerUser}). Завершите его перед созданием нового.");

        var totalActiveCount = await db.Events.CountAsync(e =>
            e.GuildId == guildId &&
            e.Status == EventStatus.Active);

        if (totalActiveCount >= limits.MaxActiveTotal)
            return (false, $"На сервере уже запущено максимальное количество активных ивентов ({limits.MaxActiveTotal}).");

        return (true, string.Empty);
    }

    public async Task<List<CustomEventTemplateEntity>> GetRecentCustomTemplatesAsync(ulong userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CustomEventTemplates
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.LastUsedAtUtc)
            .Take(5)
            .AsNoTracking()
            .ToListAsync();
    }

    private async Task SaveCustomTemplateAsync(ulong userId, string name, string description, int maxParticipants)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var cleanName = name.Trim();
            var cleanDesc = description?.Trim() ?? string.Empty;

            var existing = await db.CustomEventTemplates
                .FirstOrDefaultAsync(t => t.UserId == userId && t.Name.ToLower() == cleanName.ToLower());

            if (existing is not null)
            {
                existing.Name = cleanName;
                existing.Description = cleanDesc;
                existing.MaxParticipants = maxParticipants;
                existing.LastUsedAtUtc = DateTime.UtcNow;
            }
            else
            {
                db.CustomEventTemplates.Add(new CustomEventTemplateEntity
                {
                    UserId = userId,
                    Name = cleanName,
                    Description = cleanDesc,
                    MaxParticipants = maxParticipants,
                    LastUsedAtUtc = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync();

            var allUserTemplates = await db.CustomEventTemplates
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.LastUsedAtUtc)
                .ToListAsync();

            if (allUserTemplates.Count > 5)
            {
                db.CustomEventTemplates.RemoveRange(allUserTemplates.Skip(5));
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить шаблон кастомной игры для пользователя {UserId}.", userId);
        }
    }

    public async Task<(bool Success, string Message, EventEntity? CreatedEvent)> CreateEventFromPresetAsync(
        SocketGuild guild,
        IGuildUser hostUser,
        EventPreset preset)
    {
        var maxParticipants = preset.MaxParticipants > 0
            ? Math.Clamp(preset.MaxParticipants, Settings.Event.Limits.MinParticipants, Settings.Event.Limits.MaxParticipants)
            : Settings.Event.Limits.DefaultParticipants;

        return await CreateEventInternalAsync(
            guild: guild,
            hostUser: hostUser,
            presetId: preset.Id,
            categoryKey: preset.Category,
            name: preset.Name,
            description: preset.Description ?? string.Empty,
            emoji: string.IsNullOrWhiteSpace(preset.Emoji) ? "🎮" : preset.Emoji,
            iconUrl: preset.IconUrl ?? string.Empty,
            colorHex: string.IsNullOrWhiteSpace(preset.Color) ? "#2B2D31" : preset.Color,
            maxParticipants: maxParticipants,
            pingRoleIdOverride: preset.PingRoleId);
    }

    public async Task<(bool Success, string Message, EventEntity? CreatedEvent)> CreateCustomEventAsync(
        SocketGuild guild,
        IGuildUser hostUser,
        string name,
        string? description,
        int maxParticipants)
    {
        var cleanName = name?.Trim() ?? string.Empty;
        var cleanDesc = description?.Trim() ?? string.Empty;

        var result = await CreateEventInternalAsync(
            guild: guild,
            hostUser: hostUser,
            presetId: "custom",
            categoryKey: "custom",
            name: cleanName,
            description: cleanDesc,
            emoji: "🎮",
            iconUrl: string.Empty,
            colorHex: "#2B2D31",
            maxParticipants: maxParticipants,
            pingRoleIdOverride: 0);

        if (result.Success)
        {
            await SaveCustomTemplateAsync(hostUser.Id, cleanName, cleanDesc, maxParticipants);
        }

        return result;
    }

    private async Task<(bool Success, string Message, EventEntity? CreatedEvent)> CreateEventInternalAsync(
        SocketGuild guild,
        IGuildUser hostUser,
        string presetId,
        string categoryKey,
        string name,
        string description,
        string emoji,
        string iconUrl,
        string colorHex,
        int maxParticipants,
        ulong pingRoleIdOverride)
    {
        await _createCloseLock.WaitAsync();
        RestCategoryChannel? createdCategory = null;
        RestTextChannel? createdManageChannel = null;
        RestTextChannel? createdInfoChannel = null;
        RestTextChannel? createdChatChannel = null;
        RestVoiceChannel? createdVoiceChannel = null;

        try
        {
            var (canCreate, validationError) = await ValidateBeforeCreateAsync(
                guild.Id, hostUser.Id, name, description, maxParticipants);

            if (!canCreate)
                return (false, validationError, null);

            await using var db = await _dbFactory.CreateDbContextAsync();

            var botUserId = _client.CurrentUser.Id;
            var targetPosition = await CalculateNextCategoryPositionAsync(db, guild);

            var categoryDisplayName = $"{emoji} {name}";
            var categoryOverwrites = _permissionService.BuildCategoryOverwrites(guild, hostUser.Id, botUserId);

            createdCategory = await guild.CreateCategoryChannelAsync(categoryDisplayName, props =>
            {
                props.PermissionOverwrites = categoryOverwrites;
                if (targetPosition.HasValue)
                    props.Position = targetPosition.Value;
            });

            var manageOverwrites = _permissionService.BuildManageChannelOverwrites(guild, hostUser.Id, botUserId);
            createdManageChannel = await guild.CreateTextChannelAsync("🛠-управление", props =>
            {
                props.CategoryId = createdCategory.Id;
                props.PermissionOverwrites = manageOverwrites;
                props.Position = 0;
            });

            var infoOverwrites = _permissionService.BuildInfoChannelOverwrites(guild, hostUser.Id, botUserId);
            createdInfoChannel = await guild.CreateTextChannelAsync("📢-информация", props =>
            {
                props.CategoryId = createdCategory.Id;
                props.PermissionOverwrites = infoOverwrites;
                props.Position = 1;
            });

            var chatOverwrites = _permissionService.BuildChatChannelOverwrites(guild, hostUser.Id, botUserId);
            createdChatChannel = await guild.CreateTextChannelAsync("💬-чат", props =>
            {
                props.CategoryId = createdCategory.Id;
                props.PermissionOverwrites = chatOverwrites;
                props.Position = 2;
            });

            var voiceOverwrites = _permissionService.BuildVoiceChannelOverwrites(guild, hostUser.Id, botUserId);
            createdVoiceChannel = await guild.CreateVoiceChannelAsync($"🔊 {name}", props =>
            {
                props.CategoryId = createdCategory.Id;
                props.PermissionOverwrites = voiceOverwrites;
                props.UserLimit = maxParticipants;
                props.Position = 3;
            });

            var ev = new EventEntity
            {
                GuildId = guild.Id,
                HostUserId = hostUser.Id,
                PresetId = presetId,
                CategoryKey = categoryKey,
                Name = name,
                Description = description,
                Emoji = emoji,
                IconUrl = iconUrl,
                ColorHex = colorHex,
                MaxParticipants = maxParticipants,
                CategoryChannelId = createdCategory.Id,
                ManageChannelId = createdManageChannel.Id,
                InfoChannelId = createdInfoChannel.Id,
                ChatChannelId = createdChatChannel.Id,
                VoiceChannelId = createdVoiceChannel.Id,
                Status = EventStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
                LastActivityAtUtc = DateTime.UtcNow
            };

            db.Events.Add(ev);
            await db.SaveChangesAsync();

            var mainEmbed = _embedBuilder.BuildManagementMainEmbed(ev, 0, 0, 0, 0, 0, guild);
            var mainComponents = _embedBuilder.BuildManagementMainComponents(ev, guild);
            var manageMainMsg = await SendMessageWithRetryAsync(createdManageChannel, null, mainEmbed, mainComponents, null);

            var (modEmbed, modComponents) = _embedBuilder.BuildModerationMessage(
                guild, ev, Array.Empty<ParticipantEntity>(), Array.Empty<PunishmentEntity>(), Array.Empty<WarningEntity>());
            var manageModMsg = await SendMessageWithRetryAsync(createdManageChannel, null, modEmbed, modComponents, null);

            var (infoEmbed, infoComponents) = _embedBuilder.BuildInfoChannelMessage(ev, 0, 0);
            var pingRoleId = ResolveEventPingRoleId(guild, presetId, pingRoleIdOverride);
            string? pingContent = null;
            AllowedMentions? allowedMentions = null;

            if (pingRoleId != 0)
            {
                pingContent = $"<@&{pingRoleId}>";
                allowedMentions = new AllowedMentions(AllowedMentionTypes.None)
                {
                    RoleIds = new List<ulong> { pingRoleId }
                };
            }

            var infoMsg = await SendMessageWithRetryAsync(createdInfoChannel, pingContent, infoEmbed, infoComponents, allowedMentions);
            if (infoMsg is null || infoMsg.Id == 0)
            {
                throw new InvalidOperationException("Не удалось подтвердить отправку сообщения в канал #📢-информация.");
            }

            ev.ManageMainMessageId = manageMainMsg?.Id ?? 0;
            ev.ManageModMessageId = manageModMsg?.Id ?? 0;
            ev.InfoMessageId = infoMsg.Id;
            await db.SaveChangesAsync();

            if (targetPosition.HasValue)
            {
                _ = RepositionCategoriesInOrderAsync(guild, createdCategory.Id);
            }

            _ = _loggingService.LogEventAsync(
                LogCategory.EventCreateDelete,
                "🎉 Ивент создан",
                $"Создан ивент **{ev.Emoji} {ev.Name}** ({EmbedAndComponentBuilderService.FormatCategoryName(ev.CategoryKey)}).\nЛимит участников: `{ev.MaxParticipants}`.",
                actorUserId: hostUser.Id,
                whereInfo: $"<#{ev.InfoChannelId}> (Ивент #{ev.Id})");

            if (pingContent is not null)
            {
                _ = _loggingService.LogEventAsync(
                    LogCategory.Pings,
                    "🔔 Пинг роли ивента",
                    $"Отправлен пинг роли <@&{pingRoleId}> при создании ивента **{ev.Name}**.",
                    actorUserId: hostUser.Id,
                    whereInfo: $"<#{ev.InfoChannelId}>");
            }

            return (true, $"Ивент **{ev.Emoji} {ev.Name}** успешно создан! Управление: <#{ev.ManageChannelId}> • Информация: <#{ev.InfoChannelId}>", ev);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при создании каналов ивента. Выполняется откат созданных каналов.");
            await RollbackCreatedChannelsAsync(
                createdVoiceChannel,
                createdChatChannel,
                createdInfoChannel,
                createdManageChannel,
                createdCategory);

            await _loggingService.LogEventAsync(
                LogCategory.Errors,
                "❌ Ошибка создания ивента",
                $"Произошла ошибка при создании ивента `{name}`: {ex.Message}.",
                actorUserId: hostUser.Id);

            return (false, "Произошла ошибка при создании каналов ивента. Попробуйте ещё раз.", null);
        }
        finally
        {
            _createCloseLock.Release();
        }
    }

    public async Task<(bool Success, string Message, EventRoomEntity? Room)> CreateAdditionalRoomAsync(
        int eventId,
        IGuildUser actor,
        string roomName,
        int maxParticipants)
    {
        var cleanName = roomName?.Trim() ?? string.Empty;
        if (cleanName.Length is < 1 or > 100)
            return (false, "Название комнаты должно содержать от 1 до 100 символов.", null);

        if (maxParticipants is < 1 or > 99)
            return (false, "Количество участников комнаты должно быть от 1 до 99.", null);

        if (!_antiAbuseService.ValidateText(cleanName, null, out var abuseErr))
            return (false, abuseErr, null);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Активный ивент не найден.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только ведущий может управлять комнатами этого ивента.", null);

        var mainVoice = guild.GetVoiceChannel(ev.VoiceChannelId);
        var nextPos = (mainVoice?.Position ?? 3) + ev.AdditionalRooms.Count + 1;

        var voiceOverwrites = _permissionService.BuildVoiceChannelOverwrites(
            guild,
            ev.HostUserId,
            _client.CurrentUser.Id,
            isVoiceClosed: false);

        var createdRoomChannel = await guild.CreateVoiceChannelAsync($"🔊 {cleanName}", props =>
        {
            props.CategoryId = ev.CategoryChannelId;
            props.PermissionOverwrites = voiceOverwrites;
            props.UserLimit = maxParticipants;
            props.Position = nextPos;
        });

        var roomEntity = new EventRoomEntity
        {
            EventId = ev.Id,
            VoiceChannelId = createdRoomChannel.Id,
            Name = cleanName,
            MaxParticipants = maxParticipants,
            IsVoiceClosed = false,
            IsAllMuted = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.EventRooms.Add(roomEntity);
        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await _loggingService.LogEventAsync(
            LogCategory.OrganizerActions,
            "➕ Создана дополнительная комната",
            $"Создана комната **{cleanName}** (<#{createdRoomChannel.Id}>, лимит: `{maxParticipants}`) в ивенте **{ev.Name}**.",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        return (true, $"Дополнительная комната <#{createdRoomChannel.Id}> (**{cleanName}**, лимит: `{maxParticipants}`) создана!", roomEntity);
    }

    public async Task<(bool Success, string Message, EventRoomEntity? Room)> EditAdditionalRoomAsync(
        int eventId,
        int roomId,
        IGuildUser actor,
        string newName,
        int newMaxParticipants)
    {
        var cleanName = newName?.Trim() ?? string.Empty;
        if (cleanName.Length is < 1 or > 100)
            return (false, "Название комнаты должно содержать от 1 до 100 символов.", null);

        if (newMaxParticipants is < 1 or > 99)
            return (false, "Количество участников комнаты должно быть от 1 до 99.", null);

        if (!_antiAbuseService.ValidateText(cleanName, null, out var abuseErr))
            return (false, abuseErr, null);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Активный ивент не найден.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только ведущий может редактировать комнату этого ивента.", null);

        var room = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
        if (room is null)
            return (false, "Дополнительная комната не найдена.", null);

        room.Name = cleanName;
        room.MaxParticipants = newMaxParticipants;
        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        if (guild.GetVoiceChannel(room.VoiceChannelId) is SocketVoiceChannel voiceChannel)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await voiceChannel.ModifyAsync(props =>
                    {
                        props.Name = $"🔊 {cleanName}";
                        props.UserLimit = newMaxParticipants;
                    }).WaitAsync(TimeSpan.FromSeconds(4));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось обновить голосовой канал дополнительной комнаты {ChannelId}.", room.VoiceChannelId);
                }
            });
        }

        return (true, $"Комната <#{room.VoiceChannelId}> обновлена: **{cleanName}** (лимит: `{newMaxParticipants}`).", room);
    }

    public async Task<(bool Success, string Message, EventRoomEntity? Room)> ToggleAdditionalRoomVoiceClosedAsync(
        int eventId,
        int roomId,
        IGuildUser actor)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.AdditionalRooms)
            .Include(e => e.Participants)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этой комнатой.", null);

        var room = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
        if (room is null)
            return (false, "Комната не найдена.", null);

        room.IsVoiceClosed = !room.IsVoiceClosed;
        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var participantIds = ev.Participants.Select(p => p.UserId).ToList();
        await _permissionService.ApplyVoiceClosedStateAsync(
            guild,
            room.VoiceChannelId,
            ev.HostUserId,
            _client.CurrentUser.Id,
            room.IsVoiceClosed,
            participantIds);

        return (true, room.IsVoiceClosed ? $"Комната **{room.Name}** закрыта для новых участников." : $"Комната **{room.Name}** открыта.", room);
    }

    public async Task<(bool Success, string Message, EventRoomEntity? Room)> ToggleAdditionalRoomMuteAllAsync(
        int eventId,
        int roomId,
        IGuildUser actor)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.AdditionalRooms)
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этой комнатой.", null);

        var room = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
        if (room is null)
            return (false, "Комната не найдена.", null);

        room.IsAllMuted = !room.IsAllMuted;
        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        if (guild.GetVoiceChannel(room.VoiceChannelId) is SocketVoiceChannel voiceChannel)
        {
            var connectedIds = voiceChannel.ConnectedUsers.Select(u => u.Id).ToHashSet();

            foreach (var member in voiceChannel.ConnectedUsers)
            {
                if (member.IsBot || member.Id == ev.HostUserId || _hierarchyService.IsDeveloper(member))
                    continue;

                var hasIndividualMute = ev.Punishments.Any(p =>
                    p.TargetUserId == member.Id &&
                    (p.Type == PunishmentType.TimedMute || p.Type == PunishmentType.VoiceMicMute) &&
                    p.IsActive);

                var targetMuteState = room.IsAllMuted || hasIndividualMute;
                if (member.IsMuted != targetMuteState)
                {
                    try
                    {
                        await member.ModifyAsync(x => x.Mute = targetMuteState);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось изменить состояние mute участника {UserId} в дополнительной комнате.", member.Id);
                    }
                }
            }

            if (!room.IsAllMuted)
            {
                foreach (var p in ev.Participants)
                {
                    if (!connectedIds.Contains(p.UserId))
                    {
                        var hasIndividualMute = ev.Punishments.Any(pu =>
                            pu.TargetUserId == p.UserId &&
                            (pu.Type == PunishmentType.TimedMute || pu.Type == PunishmentType.VoiceMicMute) &&
                            pu.IsActive);

                        if (!hasIndividualMute)
                        {
                            _punishmentService.RegisterPendingVoiceUnmute(ev.GuildId, p.UserId);
                        }
                    }
                }
            }
        }

        return (true, room.IsAllMuted ? $"Все участники в комнате **{room.Name}** замьючены." : $"Общий мут в комнате **{room.Name}** снят.", room);
    }

    public async Task<(bool Success, string Message)> GatherParticipantsToMainVoiceAsync(
        int eventId,
        IGuildUser actor,
        int? specificRoomId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Активный ивент не найден.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может выполнять созыв.");

        if (guild.GetVoiceChannel(ev.VoiceChannelId) is not SocketVoiceChannel mainVoice)
            return (false, "Основной голосовой канал не найден.");

        var targetRooms = specificRoomId.HasValue
            ? ev.AdditionalRooms.Where(r => r.Id == specificRoomId.Value).ToList()
            : ev.AdditionalRooms.ToList();

        if (targetRooms.Count == 0)
            return (false, "Дополнительная комната не найдена.");

        var movedCount = 0;
        foreach (var room in targetRooms)
        {
            if (guild.GetVoiceChannel(room.VoiceChannelId) is not SocketVoiceChannel subVc)
                continue;

            foreach (var member in subVc.ConnectedUsers.ToList())
            {
                if (member.IsBot)
                    continue;

                try
                {
                    await member.ModifyAsync(x => x.Channel = mainVoice);
                    movedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось переместить участника {UserId} в основную комнату.", member.Id);
                }
            }
        }

        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            "🧲 Созыв в основную комнату",
            $"Перемещено участников в основной голосовой канал ивента **{ev.Name}**: `{movedCount}`.",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        return (true, $"Созыв выполнен: перемещено участников в основную комнату — **{movedCount}**.");
    }

    public async Task<(bool Success, string Message)> DeleteAdditionalRoomAsync(
        int eventId,
        int roomId,
        IGuildUser actor)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.AdditionalRooms)
            .Include(e => e.Punishments)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Активный ивент не найден.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только ведущий может удалить комнату этого ивента.");

        var room = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
        if (room is null)
            return (false, "Дополнительная комната уже удалена или не найдена.");

        var roomName = room.Name;
        var voiceChannelId = room.VoiceChannelId;

        if (guild.GetVoiceChannel(voiceChannelId) is SocketVoiceChannel voiceChannel)
        {
            foreach (var member in voiceChannel.ConnectedUsers)
            {
                if (member.IsMuted)
                {
                    var hasIndividualMute = ev.Punishments.Any(p =>
                        p.TargetUserId == member.Id &&
                        (p.Type == PunishmentType.TimedMute || p.Type == PunishmentType.VoiceMicMute) &&
                        p.IsActive);

                    if (!hasIndividualMute)
                    {
                        try { await member.ModifyAsync(x => x.Mute = false); } catch { }
                    }
                }
            }
        }

        db.EventRooms.Remove(room);
        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        if (guild.GetVoiceChannel(voiceChannelId) is SocketVoiceChannel vcToDelete)
        {
            try
            {
                await vcToDelete.DeleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить голосовой канал дополнительной комнаты {ChannelId}.", voiceChannelId);
            }
        }

        return (true, $"Дополнительная комната **{roomName}** удалена.");
    }

    private static async Task<RestUserMessage?> SendMessageWithRetryAsync(
        RestTextChannel channel,
        string? text,
        Embed embed,
        MessageComponent components,
        AllowedMentions? allowedMentions)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var msg = await channel.SendMessageAsync(
                    text: text,
                    embed: embed,
                    components: components,
                    allowedMentions: allowedMentions);

                if (msg is not null && msg.Id != 0)
                    return msg;
            }
            catch when (attempt == 1)
            {
                await Task.Delay(500);
            }
        }

        return null;
    }

    public ulong ResolveEventPingRoleId(SocketGuild guild, string presetId, ulong presetPingOverride = 0)
    {
        if (presetPingOverride != 0 && guild.GetRole(presetPingOverride) is not null)
            return presetPingOverride;

        if (!string.Equals(presetId, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var preset = Settings.Event.Presets.FirstOrDefault(p =>
                string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));

            if (preset is not null && preset.PingRoleId != 0 && guild.GetRole(preset.PingRoleId) is not null)
            {
                return preset.PingRoleId;
            }
        }

        var basePingId = Settings.Event.Roles.Ping;
        if (basePingId != 0 && guild.GetRole(basePingId) is not null)
            return basePingId;

        return 0;
    }

    public async Task<(bool Success, string Message)> SendTemporaryEventPingAsync(int eventId, IGuildUser actor)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);
        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом");

        if (guild.GetTextChannel(ev.InfoChannelId) is not SocketTextChannel infoChannel)
            return (false, "Канал #📢-информация не найден.");

        var roleIdToPing = ResolveEventPingRoleId(guild, ev.PresetId);
        if (roleIdToPing == 0)
            return (false, "В `settings.json` не настроена роль для пинга (`PingRoleId` или `Roles.Ping`).");

        try
        {
            var allowedMentions = new AllowedMentions(AllowedMentionTypes.None)
            {
                RoleIds = new List<ulong> { roleIdToPing }
            };

            var sentMessage = await infoChannel.SendMessageAsync(
                text: $"<@&{roleIdToPing}>",
                allowedMentions: allowedMentions);

            if (sentMessage is null || sentMessage.Id == 0)
                return (false, "Не удалось подтвердить отправку пинга в канал информации.");

            ev.LastActivityAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    await sentMessage.DeleteAsync();
                }
                catch
                {
                }
            });

            await _loggingService.LogEventAsync(
                LogCategory.Pings,
                "🔔 Повторное уведомление об ивенте",
                $"Ведущий <@{actor.Id}> отправил временный пинг <@&{roleIdToPing}> (автоудаление через 10 сек.) в ивенте **{ev.Name}**.",
                actorUserId: actor.Id,
                whereInfo: $"<#{ev.InfoChannelId}>");

            return (true, $"Уведомление <@&{roleIdToPing}> отправлено в <#{ev.InfoChannelId}> (автоматически удалится через 10 секунд).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка при отправке временного пинга в ивенте #{EventId}.", ev.Id);
            return (false, "Не удалось отправить пинг в канал информации.");
        }
    }

    public async Task<(bool Success, string Message)> UpdateInstructionAsync(
        int eventId,
        IGuildUser actor,
        string? link,
        string? code,
        string? instructionText)
    {
        var cleanLink = link?.Trim() ?? string.Empty;
        var cleanCode = code?.Trim() ?? string.Empty;
        var cleanText = instructionText?.Trim() ?? string.Empty;

        if (cleanLink.Length > 300)
            return (false, "Ссылка не может превышать 300 символов.");

        if (cleanCode.Length > 100)
            return (false, "Код не может превышать 100 символов.");

        if (cleanText.Length > 1000)
            return (false, "Текст инструкции не может превышать 1000 символов.");

        if (!_antiAbuseService.ValidateText(cleanLink, cleanText, out var abuseErr))
            return (false, abuseErr);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);
        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом");

        ev.InstructionLink = cleanLink;
        ev.InstructionCode = cleanCode;
        ev.InstructionText = cleanText;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = "Обновил инструкцию по заходу",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            "📖 Обновлена инструкция ивента",
            $"Обновлена инструкция по заходу для ивента **{ev.Name}**.",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, "Инструкция по заходу успешно сохранена! Участники могут открыть её по кнопке **«Инструкция»** в канале информации.");
    }

    public async Task<(bool IsAllowed, string ErrorMessage, Embed? InstructionEmbed)> GetInstructionForParticipantAsync(
        int eventId,
        IGuildUser user)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var isParticipantOrHost = ev.HostUserId == user.Id ||
                                  _hierarchyService.IsDeveloper(user) ||
                                  ev.Participants.Any(p => p.UserId == user.Id);

        if (!isParticipantOrHost)
        {
            return (false, "Инструкция доступна только участникам ивента! Сначала нажмите кнопку **«Участвовать»**.", null);
        }

        var embed = _embedBuilder.BuildParticipantInstructionEmbed(ev);
        return (true, string.Empty, embed);
    }

    private static async Task RollbackCreatedChannelsAsync(
        RestVoiceChannel? voice,
        RestTextChannel? chat,
        RestTextChannel? info,
        RestTextChannel? manage,
        RestCategoryChannel? category)
    {
        var channels = new IDeletable?[] { voice, chat, info, manage, category };
        foreach (var ch in channels)
        {
            if (ch is null)
                continue;

            try
            {
                await ch.DeleteAsync();
            }
            catch
            {
            }
        }
    }

    private async Task<int?> CalculateNextCategoryPositionAsync(BotDbContext db, SocketGuild guild)
    {
        var anchorId = Settings.Event.AnchorCategoryId;
        var activeEvents = await db.Events
            .Where(e => e.GuildId == guild.Id && e.Status == EventStatus.Active)
            .OrderBy(e => e.CreatedAtUtc)
            .AsNoTracking()
            .ToListAsync();

        for (var i = activeEvents.Count - 1; i >= 0; i--)
        {
            var lastCat = guild.GetCategoryChannel(activeEvents[i].CategoryChannelId);
            if (lastCat is not null)
            {
                return lastCat.Position + 1;
            }
        }

        if (anchorId != 0 && guild.GetCategoryChannel(anchorId) is SocketCategoryChannel anchorCat)
        {
            return anchorCat.Position + 1;
        }

        return null;
    }

    private async Task RepositionCategoriesInOrderAsync(SocketGuild guild, ulong newlyCreatedCategoryId)
    {
        try
        {
            var anchorId = Settings.Event.AnchorCategoryId;
            if (anchorId == 0 || guild.GetCategoryChannel(anchorId) is not SocketCategoryChannel anchorCat)
                return;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var activeCategoryIds = await db.Events
                .Where(e => e.GuildId == guild.Id && e.Status == EventStatus.Active)
                .OrderBy(e => e.CreatedAtUtc)
                .Select(e => e.CategoryChannelId)
                .ToListAsync();

            if (!activeCategoryIds.Contains(newlyCreatedCategoryId))
                activeCategoryIds.Add(newlyCreatedCategoryId);

            var basePos = anchorCat.Position;
            var offset = 1;
            foreach (var catId in activeCategoryIds)
            {
                if (guild.GetCategoryChannel(catId) is SocketCategoryChannel cat)
                {
                    await cat.ModifyAsync(p => p.Position = basePos + offset);
                    offset++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось пересчитать порядок позиций категорий.");
        }
    }

    public async Task<(bool Success, string Message)> JoinEventByButtonAsync(int eventId, IGuildUser user)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        if (user.Id == ev.HostUserId)
            return (false, "Вы являетесь ведущим этого ивента.");

        if (!_hierarchyService.HasGenderAccess(user))
            return (false, "Участвовать могут только пользователи с ролью Male или Female.");

        if (!_hierarchyService.IsDeveloper(user) &&
            (_hierarchyService.HasEventBanRole(user) ||
             ev.Punishments.Any(p => p.TargetUserId == user.Id && p.Type == PunishmentType.Ban && p.IsActive)))
        {
            return (false, "Вы забанены на ивентах и не можете записаться.");
        }

        if (ev.Participants.Any(p => p.UserId == user.Id))
            return (false, "Ты уже в списке");

        var guild = _client.GetGuild(ev.GuildId);

        if (ev.IsVoiceClosed)
        {
            if (!_pendingJoinRequests.TryAdd((ev.Id, user.Id), 1))
            {
                return (false, "Твоя заявка уже отправлена ведущему, ожидай решения.");
            }

            if (guild?.GetTextChannel(ev.ManageChannelId) is SocketTextChannel manageChannel)
            {
                var (reqEmbed, reqComponents) = _embedBuilder.BuildClosedRoomJoinRequestMessage(ev.Id, user);
                await manageChannel.SendMessageAsync(embed: reqEmbed, components: reqComponents);
                return (true, "Комната закрыта. Заявка на участие отправлена ведущему.");
            }

            _pendingJoinRequests.TryRemove((ev.Id, user.Id), out _);
            return (false, "Голосовой канал закрыт.");
        }

        if (ev.Participants.Count >= ev.MaxParticipants)
            return (false, "Список участников уже заполнен.");

        var userVoiceChId = guild?.GetUser(user.Id)?.VoiceChannel?.Id;
        var isCurrentlyInVoice = userVoiceChId.HasValue &&
            (userVoiceChId.Value == ev.VoiceChannelId || ev.AdditionalRooms.Any(r => r.VoiceChannelId == userVoiceChId.Value));

        db.Participants.Add(new ParticipantEntity
        {
            EventId = ev.Id,
            UserId = user.Id,
            IsInVoice = isCurrentlyInVoice,
            JoinedAtUtc = DateTime.UtcNow,
            LeftVoiceOrRegisteredAtUtc = isCurrentlyInVoice ? null : DateTime.UtcNow
        });

        ev.SelectedParticipantUserId ??= user.Id;
        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await _loggingService.LogEventAsync(
            LogCategory.JoinLeave,
            "✅ Запись на ивент",
            $"Пользователь <@{user.Id}> нажал кнопку «Участвовать» в ивенте **{ev.Name}**.",
            actorUserId: user.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        _embedQueue.EnqueueEventEmbedUpdate(ev.Id);
        return (true, $"Вы успешно записаны на ивент **{ev.Name}**! Заходите в голосовой канал <#{ev.VoiceChannelId}> (в течение 3 минут).");
    }

    public async Task<(bool Success, string Message)> ForceAddParticipantAsync(
        int eventId,
        IGuildUser actor,
        ulong targetUserId,
        string sourceDescription)
    {
        ClearPendingJoinRequest(eventId, targetUserId);

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
            return (false, "Только организатор может управлять этим ивентом");

        if (targetUserId == ev.HostUserId)
            return (false, "Ведущий ивента уже управляет этой комнатой и не добавляется в список игроков.");

        var targetGuildUser = guild.GetUser(targetUserId);
        if (targetGuildUser is null || targetGuildUser.IsBot)
            return (false, $"Пользователь с ID `{targetUserId}` не найден на сервере.");

        var userVoiceId = targetGuildUser.VoiceChannel?.Id;
        var isInVoice = userVoiceId.HasValue &&
            (userVoiceId.Value == ev.VoiceChannelId || ev.AdditionalRooms.Any(r => r.VoiceChannelId == userVoiceId.Value));

        var existing = ev.Participants.FirstOrDefault(p => p.UserId == targetUserId);
        if (existing is null)
        {
            db.Participants.Add(new ParticipantEntity
            {
                EventId = ev.Id,
                UserId = targetUserId,
                IsInVoice = isInVoice,
                JoinedAtUtc = DateTime.UtcNow,
                LeftVoiceOrRegisteredAtUtc = null
            });
            ev.SelectedParticipantUserId ??= targetUserId;
        }
        else
        {
            existing.IsInVoice = isInVoice;
            existing.LeftVoiceOrRegisteredAtUtc = null;
        }

        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var isChatMuted = ev.Punishments.Any(p =>
            p.TargetUserId == targetUserId && p.Type == PunishmentType.TimedMute && p.IsActive);

        await _permissionService.GrantParticipantClosedAccessAsync(
            guild,
            ev.VoiceChannelId,
            ev.ChatChannelId,
            targetUserId,
            isChatMuted);

        if (guild.GetCategoryChannel(ev.CategoryChannelId) is SocketCategoryChannel catCh)
        {
            try
            {
                await catCh.AddPermissionOverwriteAsync(targetGuildUser, new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    connect: PermValue.Allow));
            }
            catch
            {
            }
        }

        foreach (var room in ev.AdditionalRooms)
        {
            if (guild.GetVoiceChannel(room.VoiceChannelId) is SocketVoiceChannel subVc)
            {
                try
                {
                    await subVc.AddPermissionOverwriteAsync(targetGuildUser, new OverwritePermissions(
                        viewChannel: PermValue.Allow,
                        connect: PermValue.Allow));
                }
                catch
                {
                }
            }
        }

        await _loggingService.LogEventAsync(
            LogCategory.JoinLeave,
            "✅ Принудительное добавление участника",
            $"Пользователь <@{targetUserId}> добавлен в список участников ивента **{ev.Name}** ({sourceDescription}).",
            actorUserId: actor.Id,
            targetUserId: targetUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Пользователь <@{targetUserId}> добавлен в список участников ивента.");
    }

    public async Task HandleManagementChannelMessageReceivedAsync(SocketMessage rawMessage)
    {
        if (rawMessage.Author.IsBot || rawMessage is not SocketUserMessage userMessage)
            return;

        if (rawMessage.Author is not SocketGuildUser authorGuildUser)
            return;

        var content = userMessage.Content?.Trim();
        if (string.IsNullOrEmpty(content))
            return;

        var cleanedId = content.Trim('<', '@', '!', '>').Trim();
        if (!ulong.TryParse(cleanedId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ManageChannelId == rawMessage.Channel.Id && e.Status == EventStatus.Active);

        if (ev is null)
            return;

        if (!_hierarchyService.CanUseManagementChannel(authorGuildUser, ev))
            return;

        var (success, responseMsg) = await ForceAddParticipantAsync(
            ev.Id,
            authorGuildUser,
            targetUserId,
            "добавлен по ID через канал управления");

        try
        {
            await userMessage.DeleteAsync();
        }
        catch
        {
        }

        if (rawMessage.Channel is IMessageChannel ch)
        {
            var tempReply = await ch.SendMessageAsync(success ? $"✅ {responseMsg}" : $"❌ {responseMsg}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    await tempReply.DeleteAsync();
                }
                catch
                {
                }
            });
        }
    }

    public async Task HandleUserVoiceStateUpdatedAsync(
        SocketUser user,
        SocketVoiceState oldState,
        SocketVoiceState newState)
    {
        if (user.IsBot || user is not SocketGuildUser guildUser)
            return;

        var leftChannelId = oldState.VoiceChannel?.Id;
        var joinedChannelId = newState.VoiceChannel?.Id;

        if (leftChannelId == joinedChannelId)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync();

        EventEntity? leftEvent = null;
        if (leftChannelId.HasValue)
        {
            leftEvent = await db.Events
                .Include(e => e.Participants)
                .Include(e => e.AdditionalRooms)
                .FirstOrDefaultAsync(e =>
                    e.Status == EventStatus.Active &&
                    (e.VoiceChannelId == leftChannelId.Value || e.AdditionalRooms.Any(r => r.VoiceChannelId == leftChannelId.Value)));
        }

        EventEntity? joinedEvent = null;
        if (joinedChannelId.HasValue)
        {
            joinedEvent = await db.Events
                .Include(e => e.Participants)
                .Include(e => e.Punishments)
                .Include(e => e.AdditionalRooms)
                .FirstOrDefaultAsync(e =>
                    e.Status == EventStatus.Active &&
                    (e.VoiceChannelId == joinedChannelId.Value || e.AdditionalRooms.Any(r => r.VoiceChannelId == joinedChannelId.Value)));
        }

        if (joinedChannelId.HasValue && joinedEvent is null)
        {
            if (_punishmentService.TryConsumePendingVoiceUnmute(guildUser.Guild.Id, guildUser.Id) && guildUser.IsMuted)
            {
                try { await guildUser.ModifyAsync(x => x.Mute = false); } catch { }
            }

            if (_punishmentService.TryConsumePendingVoiceUndeafen(guildUser.Guild.Id, guildUser.Id) && guildUser.IsDeafened)
            {
                try { await guildUser.ModifyAsync(x => x.Deaf = false); } catch { }
            }
        }

        if (leftEvent is not null && joinedEvent is not null && leftEvent.Id == joinedEvent.Id)
        {
            await ApplyRoomVoiceStateOnChannelSwitchAsync(guildUser, joinedEvent, joinedChannelId!.Value);
            return;
        }

        if (leftEvent is not null)
        {
            var participant = leftEvent.Participants.FirstOrDefault(p => p.UserId == guildUser.Id);
            if (participant is not null && participant.IsInVoice)
            {
                participant.IsInVoice = false;
                participant.LeftVoiceOrRegisteredAtUtc = DateTime.UtcNow;
                leftEvent.LastActivityAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await _loggingService.LogEventAsync(
                    LogCategory.JoinLeave,
                    "📤 Выход из голосового канала",
                    $"Участник <@{guildUser.Id}> покинул голосовой канал ивента **{leftEvent.Name}** (будет снят через 3 мин., если не вернётся).",
                    actorUserId: guildUser.Id,
                    whereInfo: $"Ивент #{leftEvent.Id} ({leftEvent.Name})");

                _embedQueue.EnqueueEventEmbedUpdate(leftEvent.Id);
            }
        }

        if (joinedEvent is not null)
        {
            var isBannedInEvent = joinedEvent.Punishments.Any(p =>
                p.TargetUserId == guildUser.Id && p.Type == PunishmentType.Ban && p.IsActive);

            if (!_hierarchyService.IsDeveloper(guildUser) && (_hierarchyService.HasEventBanRole(guildUser) || isBannedInEvent))
            {
                try
                {
                    await guildUser.ModifyAsync(x => x.Channel = null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось отключить забаненного пользователя {UserId} от голосового канала.", guildUser.Id);
                }

                await _loggingService.LogEventAsync(
                    LogCategory.Punishments,
                    "🚫 Блокировка входа в голосовой канал",
                    $"Забаненный пользователь <@{guildUser.Id}> попытался зайти в голосовой канал ивента **{joinedEvent.Name}** и был автоматически отключён.",
                    actorUserId: guildUser.Id,
                    whereInfo: $"Ивент #{joinedEvent.Id} ({joinedEvent.Name})");
                return;
            }

            if (guildUser.Id != joinedEvent.HostUserId)
            {
                ClearPendingJoinRequest(joinedEvent.Id, guildUser.Id);

                var existingParticipant = joinedEvent.Participants.FirstOrDefault(p => p.UserId == guildUser.Id);
                if (existingParticipant is null)
                {
                    existingParticipant = new ParticipantEntity
                    {
                        EventId = joinedEvent.Id,
                        UserId = guildUser.Id,
                        IsInVoice = true,
                        JoinedAtUtc = DateTime.UtcNow,
                        LeftVoiceOrRegisteredAtUtc = null
                    };
                    db.Participants.Add(existingParticipant);
                    joinedEvent.SelectedParticipantUserId ??= guildUser.Id;
                }
                else
                {
                    existingParticipant.IsInVoice = true;
                    existingParticipant.LeftVoiceOrRegisteredAtUtc = null;
                }

                if (joinedEvent.IsVoiceClosed)
                {
                    var isChatMuted = joinedEvent.Punishments.Any(p =>
                        p.TargetUserId == guildUser.Id && p.Type == PunishmentType.TimedMute && p.IsActive);

                    await _permissionService.GrantParticipantClosedAccessAsync(
                        guildUser.Guild,
                        joinedEvent.VoiceChannelId,
                        joinedEvent.ChatChannelId,
                        guildUser.Id,
                        isChatMuted);
                }
            }

            joinedEvent.LastActivityAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await ApplyRoomVoiceStateOnChannelSwitchAsync(guildUser, joinedEvent, joinedChannelId!.Value);

            await _loggingService.LogEventAsync(
                LogCategory.JoinLeave,
                "📥 Вход в голосовой канал",
                $"Пользователь <@{guildUser.Id}> подключился к голосовому каналу ивента **{joinedEvent.Name}**.",
                actorUserId: guildUser.Id,
                whereInfo: $"Ивент #{joinedEvent.Id} ({joinedEvent.Name})");

            _embedQueue.EnqueueEventEmbedUpdate(joinedEvent.Id);
        }
    }

    private async Task ApplyRoomVoiceStateOnChannelSwitchAsync(SocketGuildUser guildUser, EventEntity ev, ulong targetVoiceChannelId)
    {
        if (guildUser.Id == ev.HostUserId || _hierarchyService.IsDeveloper(guildUser))
        {
            if (_punishmentService.TryConsumePendingVoiceUnmute(guildUser.Guild.Id, guildUser.Id) && guildUser.IsMuted)
            {
                try { await guildUser.ModifyAsync(x => x.Mute = false); } catch { }
            }
            if (_punishmentService.TryConsumePendingVoiceUndeafen(guildUser.Guild.Id, guildUser.Id) && guildUser.IsDeafened)
            {
                try { await guildUser.ModifyAsync(x => x.Deaf = false); } catch { }
            }
            return;
        }

        var isRoomMuted = targetVoiceChannelId == ev.VoiceChannelId
            ? ev.IsAllMuted
            : ev.AdditionalRooms.FirstOrDefault(r => r.VoiceChannelId == targetVoiceChannelId)?.IsAllMuted ?? false;

        var hasIndividualMute = ev.Punishments.Any(p =>
            p.TargetUserId == guildUser.Id &&
            (p.Type == PunishmentType.TimedMute || p.Type == PunishmentType.VoiceMicMute) &&
            p.IsActive);

        var shouldMute = isRoomMuted || hasIndividualMute;
        _punishmentService.ClearPendingVoiceUnmute(guildUser.Guild.Id, guildUser.Id);
        if (shouldMute != guildUser.IsMuted)
        {
            try { await guildUser.ModifyAsync(x => x.Mute = shouldMute); } catch { }
        }

        var shouldDeafen = ev.IsAllDeafened;
        _punishmentService.ClearPendingVoiceUndeafen(guildUser.Guild.Id, guildUser.Id);
        if (shouldDeafen != guildUser.IsDeafened)
        {
            try { await guildUser.ModifyAsync(x => x.Deaf = shouldDeafen); } catch { }
        }
    }

    public async Task RemoveAbsentParticipantsAfterThreeMinutesAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-3);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var activeEvents = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.AdditionalRooms)
            .Where(e => e.Status == EventStatus.Active)
            .ToListAsync(ct);

        foreach (var ev in activeEvents)
        {
            var guild = _client.GetGuild(ev.GuildId);
            var connectedIds = new HashSet<ulong>();

            if (guild?.GetVoiceChannel(ev.VoiceChannelId) is SocketVoiceChannel mainVc)
            {
                foreach (var u in mainVc.ConnectedUsers.Where(u => !u.IsBot))
                    connectedIds.Add(u.Id);
            }

            foreach (var room in ev.AdditionalRooms)
            {
                if (guild?.GetVoiceChannel(room.VoiceChannelId) is SocketVoiceChannel subVc)
                {
                    foreach (var u in subVc.ConnectedUsers.Where(u => !u.IsBot))
                        connectedIds.Add(u.Id);
                }
            }

            var modified = false;
            foreach (var p in ev.Participants.ToList())
            {
                if (p.UserId == ev.HostUserId)
                {
                    db.Participants.Remove(p);
                    modified = true;
                    continue;
                }

                if (connectedIds.Contains(p.UserId))
                {
                    if (!p.IsInVoice || p.LeftVoiceOrRegisteredAtUtc is not null)
                    {
                        p.IsInVoice = true;
                        p.LeftVoiceOrRegisteredAtUtc = null;
                        modified = true;
                    }
                    continue;
                }

                if (p.IsInVoice)
                {
                    p.IsInVoice = false;
                    p.LeftVoiceOrRegisteredAtUtc = DateTime.UtcNow;
                    modified = true;
                    continue;
                }

                if (p.LeftVoiceOrRegisteredAtUtc is null)
                    continue;

                if (p.LeftVoiceOrRegisteredAtUtc.Value <= cutoff)
                {
                    db.Participants.Remove(p);
                    if (ev.SelectedParticipantUserId == p.UserId)
                    {
                        ev.SelectedParticipantUserId = null;
                    }
                    modified = true;

                    if (ev.IsVoiceClosed && guild is not null && p.UserId != ev.HostUserId)
                    {
                        var hasChatMute = ev.Punishments.Any(pu =>
                            pu.TargetUserId == p.UserId && pu.Type == PunishmentType.TimedMute && pu.IsActive);

                        await _permissionService.RevokeParticipantClosedAccessAsync(
                            guild,
                            ev.VoiceChannelId,
                            ev.ChatChannelId,
                            p.UserId,
                            keepChatMuteOverwrite: hasChatMute);
                    }

                    await _loggingService.LogEventAsync(
                        LogCategory.JoinLeave,
                        "⏱ Автоснятие с ивента (3 минуты отсутствия)",
                        $"Участник <@{p.UserId}> автоматически снят с ивента **{ev.Name}** из-за отсутствия в голосовом канале более 3 минут.",
                        targetUserId: p.UserId,
                        whereInfo: $"Ивент #{ev.Id} ({ev.Name})");
                }
            }

            if (modified)
            {
                await db.SaveChangesAsync(ct);
                _embedQueue.EnqueueEventEmbedUpdate(ev.Id);
            }
        }
    }

    public async Task<(bool Success, string Message, EventEntity? UpdatedEvent)> ToggleMuteAllAsync(int eventId, IGuildUser actor)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом", null);

        ev.IsAllMuted = !ev.IsAllMuted;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = ev.IsAllMuted ? "Включил режим «Замьютить всех»" : "Выключил режим «Замьютить всех»",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        if (guild.GetVoiceChannel(ev.VoiceChannelId) is SocketVoiceChannel voiceChannel)
        {
            var connectedIds = voiceChannel.ConnectedUsers.Select(u => u.Id).ToHashSet();

            foreach (var member in voiceChannel.ConnectedUsers)
            {
                if (member.IsBot || member.Id == ev.HostUserId || _hierarchyService.IsDeveloper(member))
                    continue;

                var hasIndividualMute = ev.Punishments.Any(p =>
                    p.TargetUserId == member.Id &&
                    (p.Type == PunishmentType.TimedMute || p.Type == PunishmentType.VoiceMicMute) &&
                    p.IsActive);

                var targetMuteState = ev.IsAllMuted || hasIndividualMute;
                if (member.IsMuted != targetMuteState)
                {
                    try
                    {
                        await member.ModifyAsync(x => x.Mute = targetMuteState);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось изменить состояние mute участника {UserId}.", member.Id);
                    }
                }
            }

            if (!ev.IsAllMuted)
            {
                foreach (var p in ev.Participants)
                {
                    if (!connectedIds.Contains(p.UserId))
                    {
                        var hasIndividualMute = ev.Punishments.Any(pu =>
                            pu.TargetUserId == p.UserId &&
                            (pu.Type == PunishmentType.TimedMute || pu.Type == PunishmentType.VoiceMicMute) &&
                            pu.IsActive);

                        if (!hasIndividualMute)
                        {
                            _punishmentService.RegisterPendingVoiceUnmute(ev.GuildId, p.UserId);
                        }
                    }
                }
            }
        }

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            ev.IsAllMuted ? "🔇 Замьючены все участники" : "🔊 Снят общий мут",
            ev.IsAllMuted
                ? $"В ивенте **{ev.Name}** включён режим «Замьютить всех» (кроме ведущего)."
                : $"В ивенте **{ev.Name}** выключен режим «Замьютить всех».",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, ev.IsAllMuted ? "Все участники в голосовом канале замьючены." : "Общий мут в голосовом канале снят.", ev);
    }

    public async Task<(bool Success, string Message, EventEntity? UpdatedEvent)> ToggleDeafenAllAsync(int eventId, IGuildUser actor)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом", null);

        ev.IsAllDeafened = !ev.IsAllDeafened;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = ev.IsAllDeafened ? "Отключил звук всем в комнате" : "Включил звук всем в комнате",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        var allVoiceIds = new List<ulong> { ev.VoiceChannelId };
        allVoiceIds.AddRange(ev.AdditionalRooms.Select(r => r.VoiceChannelId));

        var connectedIds = new HashSet<ulong>();
        foreach (var vcId in allVoiceIds)
        {
            if (guild.GetVoiceChannel(vcId) is not SocketVoiceChannel voiceChannel)
                continue;

            foreach (var member in voiceChannel.ConnectedUsers)
            {
                connectedIds.Add(member.Id);
                if (member.IsBot || member.Id == ev.HostUserId || _hierarchyService.IsDeveloper(member))
                    continue;

                if (member.IsDeafened != ev.IsAllDeafened)
                {
                    try
                    {
                        await member.ModifyAsync(x => x.Deaf = ev.IsAllDeafened);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось изменить состояние deaf участника {UserId}.", member.Id);
                    }
                }
            }
        }

        if (!ev.IsAllDeafened)
        {
            foreach (var p in ev.Participants)
            {
                if (!connectedIds.Contains(p.UserId))
                {
                    _punishmentService.RegisterPendingVoiceUndeafen(ev.GuildId, p.UserId);
                }
            }
        }

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            ev.IsAllDeafened ? "🔕 Отключён звук всем участникам" : "🔔 Включён звук всем участникам",
            ev.IsAllDeafened
                ? $"В ивенте **{ev.Name}** включён режим «Отключить звуки» (кроме ведущего)."
                : $"В ивенте **{ev.Name}** выключен режим «Отключить звуки».",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, ev.IsAllDeafened ? "Звук для всех участников в комнате отключён." : "Звук для всех участников в комнате включён.", ev);
    }

    public async Task<(bool Success, string Message, EventEntity? UpdatedEvent)> ToggleVoiceClosedAsync(int eventId, IGuildUser actor)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом", null);

        ev.IsVoiceClosed = !ev.IsVoiceClosed;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = ev.IsVoiceClosed ? "Закрыл комнату ивента" : "Открыл комнату ивента",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        var participantIds = ev.Participants.Select(p => p.UserId).ToList();
        var mutedUserIds = ev.Punishments
            .Where(p => p.IsActive && p.Type == PunishmentType.TimedMute)
            .Select(p => p.TargetUserId)
            .ToHashSet();

        await _permissionService.ApplyVoiceClosedStateAsync(
            guild,
            ev.VoiceChannelId,
            ev.HostUserId,
            _client.CurrentUser.Id,
            ev.IsVoiceClosed,
            participantIds,
            chatChannelId: ev.ChatChannelId,
            mutedUserIds: mutedUserIds);

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            ev.IsVoiceClosed ? "🔒 Комната ивента закрыта" : "🔓 Комната ивента открыта",
            ev.IsVoiceClosed
                ? $"Комната ивента **{ev.Name}** закрыта: для неучастников видны только #информация и #войс (без входа), а #чат скрыт и доступен только участникам."
                : $"Комната ивента **{ev.Name}** снова открыта для всех.",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, ev.IsVoiceClosed ? "Комната закрыта (чат скрыт для неучастников, вход в войс только по списку)." : "Комната и чат снова открыты.", ev);
    }

    public async Task<(bool Success, string Message, EventEntity? UpdatedEvent)> UpdateMaxParticipantsAsync(
        int eventId,
        IGuildUser actor,
        int newLimit)
    {
        var limits = Settings.Event.Limits;
        if (newLimit < limits.MinParticipants || newLimit > limits.MaxParticipants)
            return (false, $"Лимит участников должен быть от {limits.MinParticipants} до {limits.MaxParticipants}.", null);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);
        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом", null);

        var oldLimit = ev.MaxParticipants;
        ev.MaxParticipants = newLimit;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = $"Изменил лимит участников: {oldLimit} → {newLimit}",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        if (guild.GetVoiceChannel(ev.VoiceChannelId) is SocketVoiceChannel voiceChannel)
        {
            try
            {
                await voiceChannel.ModifyAsync(x => x.UserLimit = newLimit);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось обновить лимит голосового канала для ивента #{EventId}.", ev.Id);
            }
        }

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            "👥 Изменён лимит участников",
            $"Лимит участников ивента **{ev.Name}** изменён с `{oldLimit}` на `{newLimit}`.",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Лимит участников обновлён: **{newLimit}**.", ev);
    }

    public async Task<(bool Success, string Message, EventEntity? UpdatedEvent)> UpdateNameAsync(
        int eventId,
        IGuildUser actor,
        string newName)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 100)
            return (false, "Название ивента должно быть от 1 до 100 символов.", null);

        if (!_antiAbuseService.ValidateText(trimmed, null, out var abuseErr))
            return (false, abuseErr, null);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);
        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом", null);

        var oldName = ev.Name;
        ev.Name = trimmed;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = $"Переименовал ивент: «{oldName}» → «{trimmed}»",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        _ = Task.Run(async () =>
        {
            if (guild.GetCategoryChannel(ev.CategoryChannelId) is SocketCategoryChannel category)
            {
                try { await category.ModifyAsync(x => x.Name = $"{ev.Emoji} {trimmed}").WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
            }

            if (guild.GetVoiceChannel(ev.VoiceChannelId) is SocketVoiceChannel voiceChannel)
            {
                try { await voiceChannel.ModifyAsync(x => x.Name = $"🔊 {trimmed}").WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
            }
        });

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            "✏️ Изменено название ивента",
            $"Название ивента изменено с **{oldName}** на **{trimmed}**.",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({trimmed})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Название ивента изменено на **{trimmed}**.", ev);
    }

    public async Task<(bool Success, string Message, EventEntity? UpdatedEvent)> UpdateDescriptionAsync(
        int eventId,
        IGuildUser actor,
        string? newDescription)
    {
        var trimmed = newDescription?.Trim() ?? string.Empty;
        if (trimmed.Length > 500)
            return (false, "Описание не может превышать 500 символов.", null);

        if (!_antiAbuseService.ValidateText(null, trimmed, out var abuseErr))
            return (false, abuseErr, null);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);
        if (ev is null)
            return (false, "Ивент не найден или уже завершён.", null);

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.", null);

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом", null);

        ev.Description = trimmed;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = "Изменил описание ивента",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            "📝 Изменено описание ивента",
            $"Обновлено описание ивента **{ev.Name}**.",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, "Описание ивента обновлено.", ev);
    }

    public async Task<(bool Success, string Message)> RedactEventAsync(
        int eventId,
        IGuildUser actor,
        string newName,
        string? newDescription,
        int newMaxParticipants)
    {
        var limits = Settings.Event.Limits;
        var cleanName = newName?.Trim() ?? string.Empty;
        var cleanDesc = newDescription?.Trim() ?? string.Empty;

        if (cleanName.Length is < 1 or > 100)
            return (false, "Название должно содержать от 1 до 100 символов.");

        if (cleanDesc.Length > 500)
            return (false, "Описание не может превышать 500 символов.");

        if (newMaxParticipants < limits.MinParticipants || newMaxParticipants > limits.MaxParticipants)
            return (false, $"Лимит участников должен быть от {limits.MinParticipants} до {limits.MaxParticipants}.");

        if (!_antiAbuseService.ValidateText(cleanName, cleanDesc, out var abuseErr))
            return (false, abuseErr);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);
        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "У вас нет прав для редактирования этого ивента.");

        var nameChanged = !string.Equals(ev.Name, cleanName, StringComparison.Ordinal);
        var limitChanged = ev.MaxParticipants != newMaxParticipants;

        ev.Name = cleanName;
        ev.Description = cleanDesc;
        ev.MaxParticipants = newMaxParticipants;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        if (actor.Id != ev.HostUserId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = ev.HostUserId,
                ControllerUserId = actor.Id,
                ActionDescription = $"Редактирование параметров ивента (Название: {cleanName}, Лимит: {newMaxParticipants})",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        _ = Task.Run(async () =>
        {
            if (nameChanged)
            {
                if (guild.GetCategoryChannel(ev.CategoryChannelId) is SocketCategoryChannel category)
                {
                    try { await category.ModifyAsync(x => x.Name = $"{ev.Emoji} {cleanName}").WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
                }

                if (guild.GetVoiceChannel(ev.VoiceChannelId) is SocketVoiceChannel voiceCh)
                {
                    try { await voiceCh.ModifyAsync(x => x.Name = $"🔊 {cleanName}").WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
                }
            }

            if (limitChanged && guild.GetVoiceChannel(ev.VoiceChannelId) is SocketVoiceChannel vc)
            {
                try { await vc.ModifyAsync(x => x.UserLimit = newMaxParticipants).WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
            }
        });

        await _loggingService.LogEventAsync(
            actor.Id == ev.HostUserId ? LogCategory.OrganizerActions : LogCategory.Control,
            "🛠 Редактирование ивента",
            $"Обновлены параметры ивента **{ev.Name}** (лимит: `{ev.MaxParticipants}`).",
            actorUserId: actor.Id,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Ивент **{ev.Name}** успешно отредактирован.");
    }

    public async Task<(bool Success, string Message)> TransferEventHostAsync(
        int eventId,
        IGuildUser actor,
        ulong newHostUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        var guild = _client.GetGuild(ev.GuildId);
        if (guild is null)
            return (false, "Сервер не найден.");

        if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            return (false, "Только организатор может управлять этим ивентом");

        if (newHostUserId == ev.HostUserId)
            return (false, "Этот пользователь уже является ведущим данного ивента.");

        var newHostGuildUser = guild.GetUser(newHostUserId);
        if (newHostGuildUser is null || newHostGuildUser.IsBot)
            return (false, "Выбранный пользователь не найден на сервере или является ботом.");

        if (!_hierarchyService.HasEventAccess(newHostGuildUser))
            return (false, "Новый ведущий должен иметь ранг не ниже Event mod.");

        var activeHostEventsCount = await db.Events.CountAsync(e =>
            e.GuildId == guild.Id &&
            e.HostUserId == newHostUserId &&
            e.Status == EventStatus.Active);

        if (activeHostEventsCount >= Settings.Event.Limits.MaxActivePerUser)
            return (false, "У выбранного ивентора уже есть активный ивент.");

        var oldHostId = ev.HostUserId;
        ev.HostUserId = newHostUserId;
        ev.LastActivityAtUtc = DateTime.UtcNow;

        var newHostInParticipants = ev.Participants.FirstOrDefault(p => p.UserId == newHostUserId);
        if (newHostInParticipants is not null)
        {
            db.Participants.Remove(newHostInParticipants);
            if (ev.SelectedParticipantUserId == newHostUserId)
                ev.SelectedParticipantUserId = null;
        }

        if (actor.Id != oldHostId)
        {
            db.ControlLogs.Add(new ControlLogEntity
            {
                EventId = ev.Id,
                EventName = ev.Name,
                HostUserId = oldHostId,
                ControllerUserId = actor.Id,
                ActionDescription = $"Передал ведение ивента от <@{oldHostId}> к <@{newHostUserId}>",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        await _permissionService.TransferHostPermissionsAsync(
            guild,
            oldHostId,
            newHostUserId,
            _client.CurrentUser.Id,
            ev.CategoryChannelId,
            ev.ManageChannelId,
            ev.InfoChannelId,
            ev.ChatChannelId,
            ev.VoiceChannelId);

        await _loggingService.LogEventAsync(
            actor.Id == oldHostId ? LogCategory.OrganizerActions : LogCategory.Control,
            "👑 Передача ивента",
            $"Ивент **{ev.Name}** передан от <@{oldHostId}> новому ведущему <@{newHostUserId}>.",
            actorUserId: actor.Id,
            targetUserId: newHostUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, $"Ивент **{ev.Name}** успешно передан ведущему <@{newHostUserId}>.");
    }

    public async Task<(bool Success, string Message)> SelectModerationParticipantAsync(
        int eventId,
        IGuildUser actor,
        ulong selectedUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);
        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        if (!_hierarchyService.CanUseManagementChannel(actor, ev))
            return (false, "Только организатор может управлять этим ивентом");

        ev.SelectedParticipantUserId = selectedUserId;
        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Message)> ChangeModerationPageAsync(
        int eventId,
        IGuildUser actor,
        int newPage)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
            return (false, "Ивент не найден или уже завершён.");

        if (!_hierarchyService.CanUseManagementChannel(actor, ev))
            return (false, "Только организатор может управлять этим ивентом");

        var totalPages = Math.Max(1, (int)Math.Ceiling(ev.Participants.Count / (double)EmbedAndComponentBuilderService.ParticipantsPageSize));
        ev.ModerationPage = Math.Clamp(newPage, 0, totalPages - 1);
        ev.LastActivityAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await _embedQueue.RefreshEventMessagesNowAsync(ev.Id);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Message)> CloseEventAsync(
        int eventId,
        ulong? actorUserId,
        string closeReason = "Завершён ведущим")
    {
        await _createCloseLock.WaitAsync();
        try
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
            if (guild is not null && actorUserId.HasValue)
            {
                var actorUser = guild.GetUser(actorUserId.Value);
                if (actorUser is not null && !_hierarchyService.CanManageOrControlEvent(guild, actorUser, ev))
                {
                    return (false, "Только организатор может управлять этим ивентом");
                }
            }

            foreach (var key in _pendingJoinRequests.Keys.Where(k => k.EventId == ev.Id).ToList())
            {
                _pendingJoinRequests.TryRemove(key, out _);
            }

            var now = DateTime.UtcNow;

            var allVoiceChannelIds = new List<ulong> { ev.VoiceChannelId };
            allVoiceChannelIds.AddRange(ev.AdditionalRooms.Select(r => r.VoiceChannelId));

            var unmutedConnectedUsers = new HashSet<ulong>();
            if (guild is not null)
            {
                foreach (var vcId in allVoiceChannelIds)
                {
                    if (guild.GetVoiceChannel(vcId) is SocketVoiceChannel voiceChannel)
                    {
                        foreach (var member in voiceChannel.ConnectedUsers)
                        {
                            unmutedConnectedUsers.Add(member.Id);
                            if (member.IsMuted || member.IsDeafened)
                            {
                                try
                                {
                                    await member.ModifyAsync(x =>
                                    {
                                        if (member.IsMuted) x.Mute = false;
                                        if (member.IsDeafened) x.Deaf = false;
                                    });
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Не удалось снять серверный mute/deaf с участника {UserId} при закрытии ивента.", member.Id);
                                }
                            }
                        }
                    }
                }
            }

            var activeMutes = ev.Punishments
                .Where(p => p.IsActive && (p.Type == PunishmentType.TimedMute || p.Type == PunishmentType.VoiceMicMute))
                .ToList();

            foreach (var mute in activeMutes)
            {
                mute.IsActive = false;
                mute.RevokedAtUtc = now;
                mute.RevokedByUserId = actorUserId;

                if (!unmutedConnectedUsers.Contains(mute.TargetUserId))
                {
                    _punishmentService.RegisterPendingVoiceUnmute(ev.GuildId, mute.TargetUserId);
                }
            }

            if (ev.IsAllMuted || ev.AdditionalRooms.Any(r => r.IsAllMuted))
            {
                foreach (var p in ev.Participants)
                {
                    if (!unmutedConnectedUsers.Contains(p.UserId))
                    {
                        _punishmentService.RegisterPendingVoiceUnmute(ev.GuildId, p.UserId);
                    }
                }
            }

            if (ev.IsAllDeafened)
            {
                foreach (var p in ev.Participants)
                {
                    if (!unmutedConnectedUsers.Contains(p.UserId))
                    {
                        _punishmentService.RegisterPendingVoiceUndeafen(ev.GuildId, p.UserId);
                    }
                }
            }

            var participantsMentions = ev.Participants.Count > 0
                ? string.Join(", ", ev.Participants.OrderBy(p => p.JoinedAtUtc).Select(p => $"<@{p.UserId}>"))
                : "нет";

            var bannedUsersList = ev.Punishments
                .Where(p => p.Type == PunishmentType.Ban)
                .Select(p => $"<@{p.TargetUserId}>")
                .Distinct()
                .ToList();
            var bannedMentions = bannedUsersList.Count > 0 ? string.Join(", ", bannedUsersList) : "нет";

            var kickedUsersList = ev.Punishments
                .Where(p => p.Type == PunishmentType.Kick)
                .Select(p => $"<@{p.TargetUserId}>")
                .Distinct()
                .ToList();
            var kickedMentions = kickedUsersList.Count > 0 ? string.Join(", ", kickedUsersList) : "нет";

            var nonTimedBans = ev.Punishments
                .Where(p => p.IsActive && p.Type == PunishmentType.Ban && p.ExpiresAtUtc == null)
                .ToList();

            var unbannedOnEndUserIds = nonTimedBans.Select(b => b.TargetUserId).Distinct().ToList();
            if (Settings.Event.Limits.RemoveBanRoleOnEventEnd)
            {
                foreach (var ban in nonTimedBans)
                {
                    ban.IsActive = false;
                    ban.RevokedAtUtc = now;
                    ban.RevokedByUserId = actorUserId;
                }
            }

            ev.Status = EventStatus.Closed;
            ev.ClosedAtUtc = now;
            ev.CloseReason = closeReason;

            db.EventHistory.Add(new EventHistoryEntity
            {
                OriginalEventId = ev.Id,
                GuildId = ev.GuildId,
                HostUserId = ev.HostUserId,
                Name = ev.Name,
                CategoryKey = ev.CategoryKey,
                TotalParticipantsCount = ev.Participants.Count,
                StartedAtUtc = ev.CreatedAtUtc,
                EndedAtUtc = now,
                Status = "CLOSED",
                CloseReason = closeReason
            });

            if (actorUserId.HasValue && actorUserId.Value != ev.HostUserId)
            {
                db.ControlLogs.Add(new ControlLogEntity
                {
                    EventId = ev.Id,
                    EventName = ev.Name,
                    HostUserId = ev.HostUserId,
                    ControllerUserId = actorUserId.Value,
                    ActionDescription = $"Завершил чужой ивент ({closeReason})",
                    CreatedAtUtc = now
                });
            }

            var additionalRoomIdsToDelete = ev.AdditionalRooms.Select(r => r.VoiceChannelId).ToList();
            await db.SaveChangesAsync();

            if (Settings.Event.Limits.RemoveBanRoleOnEventEnd && guild is not null)
            {
                foreach (var bannedUserId in unbannedOnEndUserIds)
                {
                    await _punishmentService.TryRemoveEventBanRoleIfNoActiveBansAsync(db, guild, bannedUserId);
                }
            }

            if (guild is not null)
            {
                var channelIdsToDelete = new List<ulong>();
                channelIdsToDelete.AddRange(additionalRoomIdsToDelete);
                channelIdsToDelete.AddRange(new[]
                {
                    ev.VoiceChannelId,
                    ev.ChatChannelId,
                    ev.InfoChannelId,
                    ev.ManageChannelId,
                    ev.CategoryChannelId
                });

                foreach (var chId in channelIdsToDelete)
                {
                    if (chId == 0)
                        continue;

                    if (guild.GetChannel(chId) is SocketGuildChannel ch)
                    {
                        try
                        {
                            await ch.DeleteAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Не удалось удалить канал {ChannelId} при закрытии ивента #{EventId}.", chId, ev.Id);
                        }
                    }
                }
            }

            await _loggingService.LogEventAsync(
                LogCategory.Close,
                "🏁 Ивент завершён",
                $"Ивент **{ev.Emoji} {ev.Name}** завершён.\n" +
                $"Ведущий: <@{ev.HostUserId}>\n" +
                $"Причина: `{closeReason}`\n" +
                $"участники: {participantsMentions}\n" +
                $"бан: {bannedMentions}\n" +
                $"kick: {kickedMentions}",
                actorUserId: actorUserId,
                whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

            if (actorUserId.HasValue && actorUserId.Value != ev.HostUserId)
            {
                await _loggingService.LogEventAsync(
                    LogCategory.Control,
                    "🛡 Контроль: Завершение ивента",
                    $"Контролёр <@{actorUserId.Value}> завершил ивент **{ev.Name}** (ведущий <@{ev.HostUserId}>).",
                    actorUserId: actorUserId.Value,
                    targetUserId: ev.HostUserId,
                    whereInfo: $"Ивент #{ev.Id} ({ev.Name})");
            }

            return (true, $"Ивент **{ev.Name}** завершён, все каналы удалены.");
        }
        finally
        {
            _createCloseLock.Release();
        }
    }

    public async Task SyncDatabaseWithServerOnStartupAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var activeEvents = await db.Events
                .Include(e => e.Participants)
                .Include(e => e.AdditionalRooms)
                .Where(e => e.Status == EventStatus.Active)
                .ToListAsync();

            foreach (var ev in activeEvents)
            {
                var guild = _client.GetGuild(ev.GuildId);
                if (guild is null)
                    continue;

                var category = guild.GetCategoryChannel(ev.CategoryChannelId);
                var voice = guild.GetVoiceChannel(ev.VoiceChannelId);
                var info = guild.GetTextChannel(ev.InfoChannelId);
                var manage = guild.GetTextChannel(ev.ManageChannelId);

                if (category is null || voice is null || info is null || manage is null)
                {
                    _logger.LogWarning("Ивент #{EventId} ({Name}) не имеет всех каналов на сервере. Закрываем как брошенный.", ev.Id, ev.Name);
                    await CloseEventAsync(ev.Id, null, "Закрыт при старте бота (каналы были удалены вручную)");
                    continue;
                }

                var connectedUserIds = voice.ConnectedUsers
                    .Where(u => !u.IsBot && u.Id != ev.HostUserId)
                    .Select(u => u.Id)
                    .ToHashSet();

                foreach (var room in ev.AdditionalRooms.ToList())
                {
                    if (guild.GetVoiceChannel(room.VoiceChannelId) is SocketVoiceChannel subVc)
                    {
                        foreach (var u in subVc.ConnectedUsers.Where(u => !u.IsBot && u.Id != ev.HostUserId))
                            connectedUserIds.Add(u.Id);
                    }
                    else
                    {
                        db.EventRooms.Remove(room);
                    }
                }

                foreach (var p in ev.Participants.ToList())
                {
                    if (p.UserId == ev.HostUserId)
                    {
                        db.Participants.Remove(p);
                        continue;
                    }

                    var inVoiceNow = connectedUserIds.Contains(p.UserId);
                    p.IsInVoice = inVoiceNow;
                    p.LeftVoiceOrRegisteredAtUtc = inVoiceNow ? null : (p.LeftVoiceOrRegisteredAtUtc ?? DateTime.UtcNow);
                }

                foreach (var connectedId in connectedUserIds)
                {
                    if (!ev.Participants.Any(p => p.UserId == connectedId))
                    {
                        db.Participants.Add(new ParticipantEntity
                        {
                            EventId = ev.Id,
                            UserId = connectedId,
                            IsInVoice = true,
                            JoinedAtUtc = DateTime.UtcNow,
                            LeftVoiceOrRegisteredAtUtc = null
                        });
                    }
                }

                await db.SaveChangesAsync();
                _embedQueue.EnqueueEventEmbedUpdate(ev.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при стартовой сверке БД с Discord-сервером.");
        }
    }
}
