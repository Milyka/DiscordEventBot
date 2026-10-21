using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using DiscordEventBot.Data;
using DiscordEventBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Commands;

[Group("event", "Управление ивентами, модерация и настройки игр")]
[RequireContext(ContextType.Guild)]
public sealed class EventSlashModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly HierarchyService _hierarchyService;
    private readonly EventService _eventService;
    private readonly PunishmentService _punishmentService;
    private readonly EmbedAndComponentBuilderService _embedBuilder;
    private readonly LogAndHistoryBuilderService _logBuilder;
    private readonly LoggingService _loggingService;
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;

    public EventSlashModule(
        IDbContextFactory<BotDbContext> dbFactory,
        HierarchyService hierarchyService,
        EventService eventService,
        PunishmentService punishmentService,
        EmbedAndComponentBuilderService embedBuilder,
        LogAndHistoryBuilderService logBuilder,
        LoggingService loggingService,
        IOptionsMonitor<BotSettings> settingsMonitor)
    {
        _dbFactory = dbFactory;
        _hierarchyService = hierarchyService;
        _eventService = eventService;
        _punishmentService = punishmentService;
        _embedBuilder = embedBuilder;
        _logBuilder = logBuilder;
        _loggingService = loggingService;
        _settingsMonitor = settingsMonitor;
    }

    private BotSettings Settings => _settingsMonitor.CurrentValue;

    private async Task<IGuildUser?> EnsureEventStaffAsync()
    {
        if (Context.User is not IGuildUser guildUser || !_hierarchyService.HasEventAccess(guildUser))
        {
            await RespondAsync("У вас недостаточно прав для использования команд `/event`.", ephemeral: true);
            return null;
        }

        return guildUser;
    }

    [SlashCommand("start", "Запустить новый ивент в выбранной категории")]
    public async Task StartCommandAsync(
        [Summary("категория", "Выберите категорию ивента")]
        [Choice("Настольные игры", "board")]
        [Choice("Ролевые игры", "roleplay")]
        [Choice("Развлекательные игры", "entertainment")]
        [Choice("Мультиплеерные игры", "multiplayer")]
        [Choice("Кастомная игра", "custom")]
        string category)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var limits = Settings.Event.Limits;
        await using var db = await _dbFactory.CreateDbContextAsync();

        var userActiveCount = await db.Events.CountAsync(e =>
            e.GuildId == Context.Guild.Id &&
            e.HostUserId == actor.Id &&
            e.Status == EventStatus.Active);

        if (!_hierarchyService.IsDeveloper(actor) && userActiveCount >= limits.MaxActivePerUser)
        {
            await RespondAsync($"У вас уже есть активный ивент (лимит: {limits.MaxActivePerUser}). Завершите его перед созданием нового.", ephemeral: true);
            return;
        }

        var totalActiveCount = await db.Events.CountAsync(e =>
            e.GuildId == Context.Guild.Id &&
            e.Status == EventStatus.Active);

        if (totalActiveCount >= limits.MaxActiveTotal)
        {
            await RespondAsync($"На сервере уже запущено максимальное число активных ивентов ({limits.MaxActiveTotal}).", ephemeral: true);
            return;
        }

        if (string.Equals(category, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var defaultLimit = limits.DefaultParticipants.ToString(CultureInfo.InvariantCulture);
            var modal = new ModalBuilder()
                .WithTitle("Создание кастомного ивента")
                .WithCustomId("ev:0:modal_custom")
                .AddTextInput("Название ивента (1–100 символов)", "name", TextInputStyle.Short, "Введите название ивента", minLength: 1, maxLength: 100, required: true)
                .AddTextInput("Описание / правила (необязательно)", "description", TextInputStyle.Paragraph, "Опишите правила или детали ивента (до 500 символов)", required: false, maxLength: 500)
                .AddTextInput("Количество участников (4–99)", "max_participants", TextInputStyle.Short, "По умолчанию: 10", minLength: 1, maxLength: 2, required: true, value: defaultLimit)
                .Build();

            await Context.Interaction.RespondWithModalAsync(modal);
            return;
        }

        var presets = Settings.Event.Presets
            .Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (presets.Count == 0)
        {
            await RespondAsync("В этой категории пока нет игр. Добавьте их через `/event settings`.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"ev:0:start_preset:{category}")
            .WithPlaceholder($"Выберите игру ({EmbedAndComponentBuilderService.FormatCategoryName(category)})")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var preset in presets.Take(25))
        {
            var desc = string.IsNullOrWhiteSpace(preset.Description)
                ? $"Максимум участников: {preset.MaxParticipants} чел."
                : preset.Description.Length > 90
                    ? preset.Description[..90] + "..."
                    : preset.Description;

            IEmote? emote = null;
            if (!string.IsNullOrWhiteSpace(preset.Emoji))
            {
                if (Emote.TryParse(preset.Emoji, out var customEmote))
                    emote = customEmote;
                else
                    emote = new Emoji(preset.Emoji);
            }

            menu.AddOption(
                label: preset.Name,
                value: preset.Id,
                description: desc,
                emote: emote);
        }

        var components = new ComponentBuilder()
            .WithSelectMenu(menu)
            .Build();

        await RespondAsync(
            $"Категория: **{EmbedAndComponentBuilderService.FormatCategoryName(category)}**. Выберите ивент для запуска:",
            components: components,
            ephemeral: true);
    }

    [SlashCommand("room", "Создать дополнительную комнату ивента или управлять существующей")]
    public async Task RoomCommandAsync(
        [Summary("name", "Выберите существующую дополнительную комнату (необязательно)")]
        [Autocomplete(typeof(EventRoomAutocompleteHandler))]
        string? name = null)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.AdditionalRooms)
            .AsNoTracking()
            .Where(e => e.GuildId == Context.Guild.Id && e.Status == EventStatus.Active)
            .OrderByDescending(e => e.HostUserId == actor.Id)
            .ThenByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync(e => e.HostUserId == actor.Id || _hierarchyService.IsDeveloper(actor));

        if (ev is null)
        {
            await RespondAsync("У вас нет активного ивента для управления комнатами.", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var modal = new ModalBuilder()
                .WithTitle("Создание дополнительной комнаты")
                .WithCustomId($"ev:{ev.Id}:modal_room_create")
                .AddTextInput("Название комнаты", "name", TextInputStyle.Short, "Например: Команда 2 / Мафия", minLength: 1, maxLength: 100, required: true)
                .AddTextInput("Количество участников (1–99)", "max_participants", TextInputStyle.Short, "По умолчанию: 10", minLength: 1, maxLength: 2, required: true, value: "10")
                .Build();

            await Context.Interaction.RespondWithModalAsync(modal);
            return;
        }

        var trimmed = name.Trim();
        EventRoomEntity? targetRoom = null;
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
        {
            targetRoom = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
        }

        targetRoom ??= ev.AdditionalRooms.FirstOrDefault(r =>
            string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));

        if (targetRoom is null)
        {
            await RespondAsync("Указанная дополнительная комната не найдена в вашем активном ивенте.", ephemeral: true);
            return;
        }

        var (embed, components) = _embedBuilder.BuildSubRoomManagementMessage(targetRoom);
        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    [SlashCommand("panel", "Открыть панель управления дополнительной комнатой ивента")]
    public async Task PanelCommandAsync(
        [Summary("room", "Выберите дополнительную комнату ивента")]
        [Autocomplete(typeof(EventRoomAutocompleteHandler))]
        string room)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var activeEvents = await db.Events
            .Include(e => e.AdditionalRooms)
            .AsNoTracking()
            .Where(e => e.GuildId == Context.Guild.Id && e.Status == EventStatus.Active)
            .OrderByDescending(e => e.HostUserId == actor.Id)
            .ThenByDescending(e => e.CreatedAtUtc)
            .ToListAsync();

        var trimmed = room?.Trim() ?? string.Empty;
        EventEntity? ownerEvent = null;
        EventRoomEntity? targetRoom = null;

        foreach (var ev in activeEvents)
        {
            if (!_hierarchyService.CanManageOrControlEvent(Context.Guild, actor, ev))
                continue;

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
            {
                targetRoom = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
            }

            targetRoom ??= ev.AdditionalRooms.FirstOrDefault(r =>
                string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));

            if (targetRoom is not null)
            {
                ownerEvent = ev;
                break;
            }
        }

        if (ownerEvent is null || targetRoom is null)
        {
            await RespondAsync("Дополнительная комната не найдена. Сначала создайте её через `/event room`.", ephemeral: true);
            return;
        }

        var (embed, components) = _embedBuilder.BuildRoomPanelMessage(ownerEvent, targetRoom, Context.Guild);
        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    [SlashCommand("settings", "Управление списком игр (добавление и редактирование пресетов)")]
    public async Task SettingsCommandAsync()
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var totalPresets = Settings.Event.Presets.Count;
        var (embed, components) = _embedBuilder.BuildGameSettingsMainMenu(totalPresets);

        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    [SlashCommand("instruction", "Добавить или изменить инструкцию по заходу для участников ивента")]
    public async Task InstructionCommandAsync(
        [Summary("event", "Целевой ивент (по умолчанию ваш активный ивент)")]
        [Autocomplete(typeof(ManageableEventAutocompleteHandler))]
        string? eventId = null)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var (ev, err) = await ResolveTargetEventAsync(actor, eventId);
        if (ev is null)
        {
            await RespondAsync(err, ephemeral: true);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle("Инструкция по заходу")
            .WithCustomId($"ev:{ev.Id}:modal_instruction")
            .AddTextInput("Ссылка (необязательно)", "link", TextInputStyle.Short, "https://...", required: false, maxLength: 300, value: ev.InstructionLink)
            .AddTextInput("Код комнаты / лобби (необязательно)", "code", TextInputStyle.Short, "Например: ABCD-1234", required: false, maxLength: 100, value: ev.InstructionCode)
            .AddTextInput("Инструкция по заходу", "instruction_text", TextInputStyle.Paragraph, "Опишите, как участникам зайти в игру / лобби", required: false, maxLength: 1000, value: ev.InstructionText)
            .Build();

        await Context.Interaction.RespondWithModalAsync(modal);
    }

    [SlashCommand("control", "Панель контроля чужого активного ивента")]
    public async Task ControlCommandAsync(
        [Summary("name", "Выберите активный ивент (ведущий ниже вас по иерархии)")]
        [Autocomplete(typeof(ControlEventAutocompleteHandler))]
        string eventIdRaw)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        if (!int.TryParse(eventIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            await RespondAsync("Выберите корректный активный ивент из выпадающего списка автодополнения.", ephemeral: true);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.Warnings)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == Context.Guild.Id && e.Status == EventStatus.Active);

        if (ev is null)
        {
            await RespondAsync("Ивент не найден или уже завершён.", ephemeral: true);
            return;
        }

        if (!_hierarchyService.IsDeveloper(actor) && !_hierarchyService.CanControl(Context.Guild, actor.Id, ev.HostUserId))
        {
            await RespondAsync("Вы можете контролировать только ивенты ведущих, чей ранг строго ниже вашего.", ephemeral: true);
            return;
        }

        db.ControlLogs.Add(new ControlLogEntity
        {
            EventId = ev.Id,
            EventName = ev.Name,
            HostUserId = ev.HostUserId,
            ControllerUserId = actor.Id,
            ActionDescription = "Открыл панель контроля (/event control)",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await _loggingService.LogEventAsync(
            LogCategory.Control,
            "🛡 Открыта панель контроля (/event control)",
            $"Контролёр <@{actor.Id}> открыл панель контроля ивента **{ev.Name}** (ведущий <@{ev.HostUserId}>).",
            actorUserId: actor.Id,
            targetUserId: ev.HostUserId,
            whereInfo: $"Ивент #{ev.Id} ({ev.Name})");

        var participantsCount = ev.Participants.Count;
        var inVoiceCount = ev.Participants.Count(p => p.IsInVoice);
        var activeBansCount = ev.Punishments.Count(p => p.IsActive && p.Type == PunishmentType.Ban);
        var kicksCount = ev.Punishments.Count(p => p.Type == PunishmentType.Kick);
        var warningsCount = ev.Warnings.Count;

        var embed = _embedBuilder.BuildManagementMainEmbed(
            ev, participantsCount, inVoiceCount, activeBansCount, kicksCount, warningsCount, Context.Guild);
        var components = _embedBuilder.BuildManagementMainComponents(ev, Context.Guild);

        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    [SlashCommand("log", "Просмотр журналов банов, киков, истории ивентов и контроля")]
    public async Task LogCommandAsync(
        [Summary("type", "Выберите тип журнала")]
        [Choice("ban list", "ban")]
        [Choice("kick list", "kick")]
        [Choice("history list", "history")]
        [Choice("control list", "control")]
        string type)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var (embed, components) = await _logBuilder.BuildLogPageAsync(Context.Guild, actor, type, 0);
        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    private async Task<(EventEntity? Event, string ErrorMessage)> ResolveTargetEventAsync(IGuildUser actor, string? eventIdRaw)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (!string.IsNullOrWhiteSpace(eventIdRaw))
        {
            if (!int.TryParse(eventIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
                return (null, "Указан некорректный ID ивента.");

            var specifiedEvent = await db.Events
                .FirstOrDefaultAsync(e => e.Id == eventId && e.GuildId == Context.Guild.Id && e.Status == EventStatus.Active);

            if (specifiedEvent is null)
                return (null, "Указанный ивент не найден или уже завершён.");

            if (!_hierarchyService.CanManageOrControlEvent(Context.Guild, actor, specifiedEvent))
                return (null, "У вас нет прав для контроля этого ивента.");

            return (specifiedEvent, string.Empty);
        }

        var ownEvent = await db.Events
            .Where(e => e.GuildId == Context.Guild.Id && e.HostUserId == actor.Id && e.Status == EventStatus.Active)
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync();

        if (ownEvent is null && _hierarchyService.IsDeveloper(actor))
        {
            ownEvent = await db.Events
                .Where(e => e.GuildId == Context.Guild.Id && e.Status == EventStatus.Active)
                .OrderByDescending(e => e.CreatedAtUtc)
                .FirstOrDefaultAsync();
        }

        if (ownEvent is null)
            return (null, "У вас нет активного ивента. Если вы хотите применить действие к чужому ивенту, укажите параметр `event:`.");

        return (ownEvent, string.Empty);
    }

    [SlashCommand("ban", "Забанить пользователя на ивентах до указанного времени")]
    public async Task BanCommandAsync(
        [Summary("user", "Пользователь для бана")] IUser user,
        [Summary("duration", "Длительность бана (например: 2h, 1d, 7d; по умолчанию 1d)")] string? duration = "1d",
        [Summary("reason", "Причина бана")] string? reason = null,
        [Summary("event", "Целевой ивент (по умолчанию ваш активный ивент)")]
        [Autocomplete(typeof(ManageableEventAutocompleteHandler))]
        string? eventId = null)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var (ev, err) = await ResolveTargetEventAsync(actor, eventId);
        if (ev is null)
        {
            await RespondAsync(err, ephemeral: true);
            return;
        }

        var rawDur = string.IsNullOrWhiteSpace(duration) ? "1d" : duration;
        if (!PunishmentService.TryParseDuration(rawDur, out var parsedDuration, out var durErr))
        {
            await RespondAsync(durErr, ephemeral: true);
            return;
        }

        var result = await _punishmentService.BanUserAsync(ev.Id, actor, user.Id, reason, parsedDuration);
        await RespondAsync(result.Message, ephemeral: true);
    }

    [SlashCommand("unban", "Снять бан с пользователя в ивенте")]
    public async Task UnbanCommandAsync(
        [Summary("user", "Пользователь для разбана")] IUser user,
        [Summary("event", "Целевой ивент (по умолчанию ваш активный ивент)")]
        [Autocomplete(typeof(ManageableEventAutocompleteHandler))]
        string? eventId = null)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var (ev, err) = await ResolveTargetEventAsync(actor, eventId);
        if (ev is null)
        {
            await RespondAsync(err, ephemeral: true);
            return;
        }

        var result = await _punishmentService.UnbanUserAsync(ev.Id, actor, user.Id);
        await RespondAsync(result.Message, ephemeral: true);
    }

    [SlashCommand("kick", "Кикнуть пользователя из ивента")]
    public async Task KickCommandAsync(
        [Summary("user", "Пользователь для кика")] IUser user,
        [Summary("event", "Целевой ивент (по умолчанию ваш активный ивент)")]
        [Autocomplete(typeof(ManageableEventAutocompleteHandler))]
        string? eventId = null)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var (ev, err) = await ResolveTargetEventAsync(actor, eventId);
        if (ev is null)
        {
            await RespondAsync(err, ephemeral: true);
            return;
        }

        var result = await _punishmentService.KickUserAsync(ev.Id, actor, user.Id);
        await RespondAsync(result.Message, ephemeral: true);
    }

    [SlashCommand("close", "Завершить активный ивент (с подтверждением)")]
    public async Task CloseCommandAsync(
        [Summary("event", "Целевой ивент (по умолчанию ваш активный ивент)")]
        [Autocomplete(typeof(ManageableEventAutocompleteHandler))]
        string? eventId = null)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var (ev, err) = await ResolveTargetEventAsync(actor, eventId);
        if (ev is null)
        {
            await RespondAsync(err, ephemeral: true);
            return;
        }

        var components = new ComponentBuilder()
            .WithButton("Да, завершить", $"ev:{ev.Id}:close_confirm", ButtonStyle.Danger, new Emoji("✅"))
            .WithButton("Нет, отмена", $"ev:{ev.Id}:close_cancel", ButtonStyle.Secondary, new Emoji("✖️"))
            .Build();

        await RespondAsync(
            $"Вы уверены, что хотите завершить ивент **{ev.Emoji} {ev.Name}** и удалить все его каналы?",
            components: components,
            ephemeral: true);
    }

    [SlashCommand("redact", "Редактировать название, описание и лимит участников ивента")]
    public async Task RedactCommandAsync(
        [Summary("event", "Целевой ивент (по умолчанию ваш активный ивент)")]
        [Autocomplete(typeof(ManageableEventAutocompleteHandler))]
        string? eventId = null)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var (ev, err) = await ResolveTargetEventAsync(actor, eventId);
        if (ev is null)
        {
            await RespondAsync(err, ephemeral: true);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle("Редактирование ивента")
            .WithCustomId($"ev:{ev.Id}:modal_redact")
            .AddTextInput("Название ивента (1–100 символов)", "name", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true, value: ev.Name)
            .AddTextInput("Описание / правила (до 500 символов)", "description", TextInputStyle.Paragraph, maxLength: 500, required: false, value: ev.Description)
            .AddTextInput("Максимум участников (4–99)", "max_participants", TextInputStyle.Short, minLength: 1, maxLength: 2, required: true, value: ev.MaxParticipants.ToString(CultureInfo.InvariantCulture))
            .Build();

        await Context.Interaction.RespondWithModalAsync(modal);
    }

    [SlashCommand("history", "Показать историю проведённых ивентов пользователя")]
    public async Task HistoryCommandAsync(
        [Summary("user", "Пользователь (по умолчанию — ваша история)")] IUser? user = null)
    {
        var actor = await EnsureEventStaffAsync();
        if (actor is null)
            return;

        var targetUserId = user?.Id ?? actor.Id;

        if (targetUserId != actor.Id &&
            !_hierarchyService.IsDeveloper(actor) &&
            !_hierarchyService.CanControl(Context.Guild, actor.Id, targetUserId))
        {
            await RespondAsync("Чужую историю ивентов можно смотреть только у пользователей с рангом ниже вашего.", ephemeral: true);
            return;
        }

        var (embed, components) = await _logBuilder.BuildUserHistoryPageAsync(Context.Guild.Id, targetUserId, 0);
        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }
}
