using System.Globalization;
using Discord;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using DiscordEventBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Services;

public sealed class EventInteractionRouter
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly DiscordSocketClient _client;
    private readonly HierarchyService _hierarchyService;
    private readonly EventService _eventService;
    private readonly PunishmentService _punishmentService;
    private readonly GamePresetsSettingsService _gamePresetsService;
    private readonly EmbedAndComponentBuilderService _embedBuilder;
    private readonly LogAndHistoryBuilderService _logBuilder;
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;
    private readonly ILogger<EventInteractionRouter> _logger;

    public EventInteractionRouter(
        IDbContextFactory<BotDbContext> dbFactory,
        DiscordSocketClient client,
        HierarchyService hierarchyService,
        EventService eventService,
        PunishmentService punishmentService,
        GamePresetsSettingsService gamePresetsService,
        EmbedAndComponentBuilderService embedBuilder,
        LogAndHistoryBuilderService logBuilder,
        IOptionsMonitor<BotSettings> settingsMonitor,
        ILogger<EventInteractionRouter> logger)
    {
        _dbFactory = dbFactory;
        _client = client;
        _hierarchyService = hierarchyService;
        _eventService = eventService;
        _punishmentService = punishmentService;
        _gamePresetsService = gamePresetsService;
        _embedBuilder = embedBuilder;
        _logBuilder = logBuilder;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    private BotSettings Settings => _settingsMonitor.CurrentValue;

    public bool CanHandle(SocketInteraction interaction)
    {
        return interaction switch
        {
            SocketMessageComponent comp => comp.Data.CustomId.StartsWith("ev:", StringComparison.Ordinal),
            SocketModal modal => modal.Data.CustomId.StartsWith("ev:", StringComparison.Ordinal),
            _ => false
        };
    }

    public async Task HandleAsync(SocketInteraction interaction)
    {
        if (interaction is SocketMessageComponent component)
        {
            await HandleComponentAsync(component);
        }
        else if (interaction is SocketModal modal)
        {
            await HandleModalAsync(modal);
        }
    }

    private async Task<(bool Allowed, EventEntity? Event, SocketGuildUser? Actor, SocketGuild? Guild)> ValidateManagementAccessAsync(
        SocketInteraction interaction,
        int eventId,
        bool requireDirectHostOrDev = false)
    {
        if (interaction.User is not SocketGuildUser actor)
        {
            await interaction.RespondAsync("Только организатор может управлять этим ивентом", ephemeral: true);
            return (false, null, null, null);
        }

        var guild = actor.Guild;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.Warnings)
            .Include(e => e.AdditionalRooms)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null)
        {
            await interaction.RespondAsync("Ивент не найден или уже завершён.", ephemeral: true);
            return (false, null, actor, guild);
        }

        var isInsideManageChannel = interaction.Channel?.Id == ev.ManageChannelId;
        if (requireDirectHostOrDev || isInsideManageChannel)
        {
            if (!_hierarchyService.CanUseManagementChannel(actor, ev))
            {
                await interaction.RespondAsync("Только организатор может управлять этим ивентом", ephemeral: true);
                return (false, ev, actor, guild);
            }
        }
        else
        {
            if (!_hierarchyService.CanManageOrControlEvent(guild, actor, ev))
            {
                await interaction.RespondAsync("Только организатор может управлять этим ивентом", ephemeral: true);
                return (false, ev, actor, guild);
            }
        }

        return (true, ev, actor, guild);
    }

    private async Task RefreshControlEphemeralIfNeededAsync(SocketMessageComponent component, int eventId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.Participants)
            .Include(e => e.Punishments)
            .Include(e => e.Warnings)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        if (ev is null || component.Channel?.Id == ev.ManageChannelId)
            return;

        var guild = (component.User as SocketGuildUser)?.Guild;
        var mainEmbed = _embedBuilder.BuildManagementMainEmbed(
            ev,
            ev.Participants.Count,
            ev.Participants.Count(p => p.IsInVoice),
            ev.Punishments.Count(p => p.IsActive && p.Type == PunishmentType.Ban),
            ev.Punishments.Count(p => p.Type == PunishmentType.Kick),
            ev.Warnings.Count,
            guild);
        var mainComponents = _embedBuilder.BuildManagementMainComponents(ev, guild);

        try
        {
            await component.ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = mainEmbed;
                msg.Components = mainComponents;
            });
        }
        catch
        {
        }
    }

    private async Task RefreshRoomPanelEphemeralAsync(SocketMessageComponent component, int eventId, int roomId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ev = await db.Events
            .Include(e => e.AdditionalRooms)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Active);

        var room = ev?.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
        if (ev is null || room is null)
            return;

        var guild = (component.User as SocketGuildUser)?.Guild;
        var (embed, components) = _embedBuilder.BuildRoomPanelMessage(ev, room, guild);

        try
        {
            await component.ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed;
                msg.Components = components;
            });
        }
        catch
        {
        }
    }

    private static SelectMenuBuilder BuildCategorySelectMenu(string customId, string placeholder)
    {
        return new SelectMenuBuilder()
            .WithCustomId(customId)
            .WithPlaceholder(placeholder)
            .WithMinValues(1)
            .WithMaxValues(1)
            .AddOption("Настольные игры", "board", "Хамелеон, Пазлы, Уно и др.", new Emoji("🎲"))
            .AddOption("Ролевые игры", "roleplay", "Мафия, Бункер и др.", new Emoji("🎭"))
            .AddOption("Развлекательные игры", "entertainment", "Джекбокс, Гартик фон, Шляпа, Крокодил и др.", new Emoji("🎉"))
            .AddOption("Мультиплеерные игры", "multiplayer", "Майнкрафт, Амонг ас, Роблокс, DnD и др.", new Emoji("🎮"));
    }

    private async Task HandleComponentAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        var parts = customId.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            await component.RespondAsync("Некорректный идентификатор кнопки.", ephemeral: true);
            return;
        }

        var action = parts[2];
        var arg1 = parts.Length > 3 ? parts[3] : string.Empty;
        var arg2 = parts.Length > 4 ? parts[4] : string.Empty;

        switch (action)
        {
            case "settings_add_btn":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var select = BuildCategorySelectMenu("ev:0:settings_add_cat", "Выберите категорию для новой игры");
                var comp = new ComponentBuilder()
                    .WithSelectMenu(select, row: 0)
                    .WithButton("Назад", "ev:0:settings_back", ButtonStyle.Secondary, new Emoji("↩️"), row: 1)
                    .Build();

                await component.UpdateAsync(msg =>
                {
                    msg.Content = "➕ **Добавление новой игры**\nВыберите категорию из выпадающего списка ниже — сразу после выбора откроется окно ввода названия, описания и количества участников:";
                    msg.Embed = null;
                    msg.Components = comp;
                });
                return;
            }

            case "settings_add_cat":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var selectedCategory = component.Data.Values.FirstOrDefault() ?? "board";
                var catTitle = EmbedAndComponentBuilderService.FormatCategoryName(selectedCategory);

                var modal = new ModalBuilder()
                    .WithTitle($"Добавить игру: {catTitle}")
                    .WithCustomId($"ev:0:modal_settings_add:{selectedCategory}")
                    .AddTextInput("Название игры", "name", TextInputStyle.Short, "Введите название игры (1–100 символов)", minLength: 1, maxLength: 100, required: true)
                    .AddTextInput("Описание игры", "description", TextInputStyle.Paragraph, "Описание и правила игры (до 500 символов)", required: false, maxLength: 500)
                    .AddTextInput("Количество участников (4–99)", "max_participants", TextInputStyle.Short, "По умолчанию: 10", minLength: 1, maxLength: 2, required: true, value: "10")
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "settings_edit_btn":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var select = BuildCategorySelectMenu("ev:0:settings_edit_cat", "1. Выберите категорию игры");
                var comp = new ComponentBuilder()
                    .WithSelectMenu(select, row: 0)
                    .WithButton("Назад", "ev:0:settings_back", ButtonStyle.Secondary, new Emoji("↩️"), row: 1)
                    .Build();

                await component.UpdateAsync(msg =>
                {
                    msg.Content = "✏️ **Редактирование игры — Шаг 1 из 2**\nВыберите категорию, в которой находится игра:";
                    msg.Embed = null;
                    msg.Components = comp;
                });
                return;
            }

            case "settings_edit_cat":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var selectedCategory = component.Data.Values.FirstOrDefault() ?? "board";
                var games = _gamePresetsService.GetPresetsByCategory(selectedCategory);

                if (games.Count == 0)
                {
                    await component.RespondAsync("В этой категории пока нет игр для редактирования.", ephemeral: true);
                    return;
                }

                var gameSelect = new SelectMenuBuilder()
                    .WithCustomId($"ev:0:settings_edit_pick:{selectedCategory}")
                    .WithPlaceholder("2. Выберите игру для редактирования")
                    .WithMinValues(1)
                    .WithMaxValues(1);

                foreach (var g in games.Take(25))
                {
                    var desc = string.IsNullOrWhiteSpace(g.Description)
                        ? $"Участников: {g.MaxParticipants}"
                        : g.Description.Length > 80
                            ? g.Description[..80] + "..."
                            : g.Description;

                    gameSelect.AddOption(g.Name, g.Id, desc);
                }

                var comp = new ComponentBuilder()
                    .WithSelectMenu(gameSelect, row: 0)
                    .WithButton("Назад к выбору категории", "ev:0:settings_edit_btn", ButtonStyle.Secondary, new Emoji("↩️"), row: 1)
                    .Build();

                await component.UpdateAsync(msg =>
                {
                    msg.Content = $"✏️ **Редактирование игры — Шаг 2 из 2** ({EmbedAndComponentBuilderService.FormatCategoryName(selectedCategory)})\nВыберите игру из списка — откроется окно редактирования:";
                    msg.Embed = null;
                    msg.Components = comp;
                });
                return;
            }

            case "settings_edit_pick":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var presetId = component.Data.Values.FirstOrDefault() ?? string.Empty;
                var preset = _gamePresetsService.GetPresetById(presetId);
                if (preset is null)
                {
                    await component.RespondAsync("Выбранная игра не найдена.", ephemeral: true);
                    return;
                }

                var modal = new ModalBuilder()
                    .WithTitle($"Редактировать: {preset.Name}")
                    .WithCustomId($"ev:0:modal_settings_edit:{preset.Id}")
                    .AddTextInput("Название игры", "name", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true, value: preset.Name)
                    .AddTextInput("Описание игры", "description", TextInputStyle.Paragraph, required: false, maxLength: 500, value: preset.Description)
                    .AddTextInput("Количество участников (4–99)", "max_participants", TextInputStyle.Short, minLength: 1, maxLength: 2, required: true, value: preset.MaxParticipants.ToString(CultureInfo.InvariantCulture))
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "settings_back":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var (embed, comp) = _embedBuilder.BuildGameSettingsMainMenu(Settings.Event.Presets.Count);
                await component.UpdateAsync(msg =>
                {
                    msg.Content = null;
                    msg.Embed = embed;
                    msg.Components = comp;
                });
                return;
            }

            case "start_preset":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав для запуска ивента.", ephemeral: true);
                    return;
                }

                var selectedPresetId = component.Data.Values.FirstOrDefault();
                var preset = Settings.Event.Presets.FirstOrDefault(p =>
                    string.Equals(p.Id, selectedPresetId, StringComparison.OrdinalIgnoreCase));

                if (preset is null)
                {
                    await component.RespondAsync("Выбранный пресет не найден в `settings.json`.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);
                var (success, message, _) = await _eventService.CreateEventFromPresetAsync(actor.Guild, actor, preset);

                await component.ModifyOriginalResponseAsync(msg =>
                {
                    msg.Content = success ? $"✅ {message}" : $"❌ {message}";
                    msg.Components = new ComponentBuilder().Build();
                });
                return;
            }

            case "open_custom_modal":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав для запуска ивента.", ephemeral: true);
                    return;
                }

                var modal = new ModalBuilder()
                    .WithTitle("Создание кастомного ивента")
                    .WithCustomId("ev:0:modal_custom")
                    .AddTextInput("Название ивента (1–100 символов)", "name", TextInputStyle.Short, "Введите название", 1, 100, true)
                    .AddTextInput("Описание / правила (необязательно)", "description", TextInputStyle.Paragraph, "До 500 символов", required: false, maxLength: 500)
                    .AddTextInput("Количество участников (4–99)", "max_participants", TextInputStyle.Short, "По умолчанию: 10", 1, 2, true, "10")
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "join":
            {
                if (component.User is not SocketGuildUser guildUser)
                {
                    await component.RespondAsync("Не удалось определить участника сервера.", ephemeral: true);
                    return;
                }

                var (_, message) = await _eventService.JoinEventByButtonAsync(eventId, guildUser);
                await component.RespondAsync(message, ephemeral: true);
                return;
            }

            case "join_approve":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await component.RespondAsync("Некорректный ID участника.", ephemeral: true);
                    return;
                }

                await component.DeferAsync();
                var (success, msg) = await _eventService.ForceAddParticipantAsync(
                    eventId,
                    actor,
                    targetUserId,
                    "одобрена заявка на вход в закрытую комнату");

                await component.ModifyOriginalResponseAsync(m =>
                {
                    m.Content = success
                        ? $"✅ Заявка пользователя <@{targetUserId}> (`{targetUserId}`) **одобрена**."
                        : $"❌ {msg}";
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            case "join_reject":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await component.RespondAsync("Некорректный ID участника.", ephemeral: true);
                    return;
                }

                _eventService.ClearPendingJoinRequest(eventId, targetUserId);

                await component.UpdateAsync(m =>
                {
                    m.Content = $"❌ Заявка пользователя <@{targetUserId}> (`{targetUserId}`) **отклонена**.";
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            case "show_instruction":
            {
                if (component.User is not SocketGuildUser guildUser)
                {
                    await component.RespondAsync("Не удалось определить участника сервера.", ephemeral: true);
                    return;
                }

                var (allowed, err, instrEmbed) = await _eventService.GetInstructionForParticipantAsync(eventId, guildUser);
                if (!allowed || instrEmbed is null)
                {
                    await component.RespondAsync(err, ephemeral: true);
                    return;
                }

                await component.RespondAsync(embed: instrEmbed, ephemeral: true);
                return;
            }

            case "mute_all":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                await component.DeferAsync();
                var (success, message, _) = await _eventService.ToggleMuteAllAsync(eventId, actor);
                await RefreshControlEphemeralIfNeededAsync(component, eventId);

                if (!success)
                    await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "deafen_all":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                await component.DeferAsync();
                var (success, message, _) = await _eventService.ToggleDeafenAllAsync(eventId, actor);
                await RefreshControlEphemeralIfNeededAsync(component, eventId);

                if (!success)
                    await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "gather_voice":
            {
                var (allowed, ev, actor, guild) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null || actor is null)
                    return;

                if (ev.AdditionalRooms.Count == 0)
                {
                    await component.RespondAsync("Дополнительных комнат пока нет (создайте через `/event room`).", ephemeral: true);
                    return;
                }

                if (ev.AdditionalRooms.Count == 1)
                {
                    await component.DeferAsync(ephemeral: true);
                    var (success, message) = await _eventService.GatherParticipantsToMainVoiceAsync(
                        eventId,
                        actor,
                        ev.AdditionalRooms[0].Id);

                    await component.FollowupAsync(success ? $"✅ {message}" : $"❌ {message}", ephemeral: true);
                    return;
                }

                var select = new SelectMenuBuilder()
                    .WithCustomId($"ev:{ev.Id}:gather_select")
                    .WithPlaceholder("Выберите комнату, из которой переместить участников")
                    .WithMinValues(1)
                    .WithMaxValues(1)
                    .AddOption("Все дополнительные комнаты", "all", "Переместить участников из всех доп. комнат в основную");

                foreach (var r in ev.AdditionalRooms.OrderBy(x => x.CreatedAtUtc).Take(24))
                {
                    var countInRoom = guild?.GetVoiceChannel(r.VoiceChannelId)?.ConnectedUsers.Count(u => !u.IsBot) ?? 0;
                    select.AddOption(
                        label: r.Name,
                        value: r.Id.ToString(CultureInfo.InvariantCulture),
                        description: $"В комнате сейчас: {countInRoom} чел.");
                }

                var comp = new ComponentBuilder()
                    .WithSelectMenu(select)
                    .Build();

                await component.RespondAsync("Выберите дополнительную комнату для созыва участников в основную руму:", components: comp, ephemeral: true);
                return;
            }

            case "gather_select":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                var selectedVal = component.Data.Values.FirstOrDefault() ?? "all";
                int? specificRoomId = null;
                if (!string.Equals(selectedVal, "all", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(selectedVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRoomId))
                {
                    specificRoomId = parsedRoomId;
                }

                await component.DeferAsync(ephemeral: true);
                var (success, message) = await _eventService.GatherParticipantsToMainVoiceAsync(eventId, actor, specificRoomId);

                await component.ModifyOriginalResponseAsync(msg =>
                {
                    msg.Content = success ? $"✅ {message}" : $"❌ {message}";
                    msg.Components = new ComponentBuilder().Build();
                });
                return;
            }

            case "toggle_voice":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                await component.DeferAsync();
                var (success, message, _) = await _eventService.ToggleVoiceClosedAsync(eventId, actor);
                await RefreshControlEphemeralIfNeededAsync(component, eventId);

                if (!success)
                    await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "room_panel_voice":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                if (!int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
                {
                    await component.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);
                var (success, message, _) = await _eventService.ToggleAdditionalRoomVoiceClosedAsync(eventId, roomId, actor);
                await RefreshRoomPanelEphemeralAsync(component, eventId, roomId);

                if (!success)
                    await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "room_panel_mute":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                if (!int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
                {
                    await component.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);
                var (success, message, _) = await _eventService.ToggleAdditionalRoomMuteAllAsync(eventId, roomId, actor);
                await RefreshRoomPanelEphemeralAsync(component, eventId, roomId);

                if (!success)
                    await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "room_panel_edit":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                if (!int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
                {
                    await component.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                var room = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
                if (room is null)
                {
                    await component.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                var modal = new ModalBuilder()
                    .WithTitle($"Редактировать комнату: {room.Name}")
                    .WithCustomId($"ev:{ev.Id}:modal_room_edit:{room.Id}")
                    .AddTextInput("Название комнаты (1–100 символов)", "name", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true, value: room.Name)
                    .AddTextInput("Количество участников (1–99)", "max_participants", TextInputStyle.Short, minLength: 1, maxLength: 2, required: true, value: room.MaxParticipants.ToString(CultureInfo.InvariantCulture))
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "room_panel_info":
            {
                var (allowed, ev, _, guild) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                if (!int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
                {
                    await component.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                var room = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
                if (room is null)
                {
                    await component.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                var infoEmbed = _embedBuilder.BuildRoomDetailedInfoEmbed(ev, room, guild);
                await component.RespondAsync(embed: infoEmbed, ephemeral: true);
                return;
            }

            case "edit_combined":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                var modal = new ModalBuilder()
                    .WithTitle("Редактирование ивента")
                    .WithCustomId($"ev:{ev.Id}:modal_redact")
                    .AddTextInput("Название ивента (1–100 символов)", "name", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true, value: ev.Name)
                    .AddTextInput("Описание / правила (до 500 символов)", "description", TextInputStyle.Paragraph, maxLength: 500, required: false, value: ev.Description)
                    .AddTextInput("Лимит участников (4–99)", "max_participants", TextInputStyle.Short, minLength: 1, maxLength: 2, required: true, value: ev.MaxParticipants.ToString(CultureInfo.InvariantCulture))
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "edit_instruction":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                var modal = new ModalBuilder()
                    .WithTitle("Инструкция по заходу")
                    .WithCustomId($"ev:{ev.Id}:modal_instruction")
                    .AddTextInput("Ссылка (необязательно)", "link", TextInputStyle.Short, "https://...", required: false, maxLength: 300, value: ev.InstructionLink)
                    .AddTextInput("Код комнаты / лобби (необязательно)", "code", TextInputStyle.Short, "Например: ABCD-1234", required: false, maxLength: 100, value: ev.InstructionCode)
                    .AddTextInput("Инструкция по заходу", "instruction_text", TextInputStyle.Paragraph, "Опишите, как участникам зайти в игру / лобби", required: false, maxLength: 1000, value: ev.InstructionText)
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "notify_ping":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                await component.DeferAsync(ephemeral: true);
                var (_, message) = await _eventService.SendTemporaryEventPingAsync(eventId, actor);
                await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "info_popup":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                var popupEmbed = _embedBuilder.BuildDetailedInfoPopupEmbed(
                    ev,
                    ev.Participants,
                    ev.Punishments.Count(p => p.IsActive && p.Type == PunishmentType.Ban),
                    ev.Punishments.Count(p => p.Type == PunishmentType.Kick),
                    ev.Warnings.Count);

                await component.RespondAsync(embed: popupEmbed, ephemeral: true);
                return;
            }

            case "edit_limit":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                var modal = new ModalBuilder()
                    .WithTitle("Изменить лимит участников")
                    .WithCustomId($"ev:{ev.Id}:modal_limit")
                    .AddTextInput(
                        "Максимум участников (4–99)",
                        "max_participants",
                        TextInputStyle.Short,
                        minLength: 1,
                        maxLength: 2,
                        required: true,
                        value: ev.MaxParticipants.ToString(CultureInfo.InvariantCulture))
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "edit_name":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                var modal = new ModalBuilder()
                    .WithTitle("Переименовать ивент")
                    .WithCustomId($"ev:{ev.Id}:modal_name")
                    .AddTextInput(
                        "Новое название (1–100 символов)",
                        "name",
                        TextInputStyle.Short,
                        minLength: 1,
                        maxLength: 100,
                        required: true,
                        value: ev.Name)
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "edit_desc":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                var modal = new ModalBuilder()
                    .WithTitle("Изменить описание ивента")
                    .WithCustomId($"ev:{ev.Id}:modal_desc")
                    .AddTextInput(
                        "Описание / правила (до 500 символов)",
                        "description",
                        TextInputStyle.Paragraph,
                        maxLength: 500,
                        required: false,
                        value: ev.Description)
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "room_edit":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                if (!int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
                {
                    await component.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                var room = ev.AdditionalRooms.FirstOrDefault(r => r.Id == roomId);
                if (room is null)
                {
                    await component.RespondAsync("Комната уже удалена или не найдена.", ephemeral: true);
                    return;
                }

                var modal = new ModalBuilder()
                    .WithTitle($"Редактировать комнату: {room.Name}")
                    .WithCustomId($"ev:{ev.Id}:modal_room_edit:{room.Id}")
                    .AddTextInput("Название комнаты (1–100 символов)", "name", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true, value: room.Name)
                    .AddTextInput("Количество участников (1–99)", "max_participants", TextInputStyle.Short, minLength: 1, maxLength: 2, required: true, value: room.MaxParticipants.ToString(CultureInfo.InvariantCulture))
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "room_delete":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                if (!int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
                {
                    await component.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);
                var (success, message) = await _eventService.DeleteAdditionalRoomAsync(eventId, roomId, actor);

                await component.ModifyOriginalResponseAsync(msg =>
                {
                    msg.Content = success ? $"✅ {message}" : $"❌ {message}";
                    msg.Embed = null;
                    msg.Components = new ComponentBuilder().Build();
                });
                return;
            }

            case "transfer_prompt":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                var userSelect = new SelectMenuBuilder()
                    .WithType(ComponentType.UserSelect)
                    .WithCustomId($"ev:{ev.Id}:transfer_select")
                    .WithPlaceholder("Выберите нового ведущего (ранг не ниже Event mod)")
                    .WithMinValues(1)
                    .WithMaxValues(1);

                var comp = new ComponentBuilder()
                    .WithSelectMenu(userSelect)
                    .Build();

                await component.RespondAsync("Выберите ивентора, которому хотите передать ведение ивента:", components: comp, ephemeral: true);
                return;
            }

            case "transfer_select":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                var rawUserId = component.Data.Values.FirstOrDefault();
                if (!ulong.TryParse(rawUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newHostId))
                {
                    await component.RespondAsync("Не удалось определить выбранного пользователя.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);
                var (_, message) = await _eventService.TransferEventHostAsync(eventId, actor, newHostId);

                await component.ModifyOriginalResponseAsync(msg =>
                {
                    msg.Content = message;
                    msg.Components = new ComponentBuilder().Build();
                });
                return;
            }

            case "close_prompt":
            {
                var (allowed, ev, _, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || ev is null)
                    return;

                var comp = new ComponentBuilder()
                    .WithButton("Да, завершить", $"ev:{ev.Id}:close_confirm", ButtonStyle.Danger, new Emoji("✅"))
                    .WithButton("Отмена", $"ev:{ev.Id}:close_cancel", ButtonStyle.Secondary, new Emoji("✖️"))
                    .Build();

                await component.RespondAsync(
                    $"Вы уверены, что хотите завершить ивент **{ev.Emoji} {ev.Name}** и удалить все его каналы?",
                    components: comp,
                    ephemeral: true);
                return;
            }

            case "close_confirm":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId);
                if (!allowed || actor is null)
                    return;

                await component.DeferAsync(ephemeral: true);
                var (_, message) = await _eventService.CloseEventAsync(eventId, actor.Id, "Завершён ведущим / контролёром");

                try
                {
                    await component.ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Content = message;
                        msg.Components = new ComponentBuilder().Build();
                    });
                }
                catch
                {
                }
                return;
            }

            case "close_cancel":
            {
                await component.UpdateAsync(msg =>
                {
                    msg.Content = "Завершение ивента отменено.";
                    msg.Components = new ComponentBuilder().Build();
                });
                return;
            }

            case "select_part":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                var rawTargetId = component.Data.Values.FirstOrDefault();
                if (!ulong.TryParse(rawTargetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId))
                {
                    await component.RespondAsync("Участник не выбран.", ephemeral: true);
                    return;
                }

                await component.DeferAsync();
                await _eventService.SelectModerationParticipantAsync(eventId, actor, targetUserId);
                return;
            }

            case "mod_page":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newPage))
                    newPage = 0;

                await component.DeferAsync();
                await _eventService.ChangeModerationPageAsync(eventId, actor, newPage);
                return;
            }

            case "mod_ban":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await component.RespondAsync("Сначала выберите участника в выпадающем списке.", ephemeral: true);
                    return;
                }

                if (await _punishmentService.HasActiveBanAsync(eventId, targetUserId))
                {
                    await component.DeferAsync(ephemeral: true);
                    var (_, msg) = await _punishmentService.UnbanUserAsync(eventId, actor, targetUserId);
                    await component.FollowupAsync(msg, ephemeral: true);
                    return;
                }

                var modal = new ModalBuilder()
                    .WithTitle("Бан участника на ивентах")
                    .WithCustomId($"ev:{eventId}:modal_ban:{targetUserId}")
                    .AddTextInput("Длительность бана (например: 2h, 1d, 7d)", "duration", TextInputStyle.Short, "Минимум 1m (например: 1d, 7d)", 1, 10, true, "1d")
                    .AddTextInput("Причина (необязательно)", "reason", TextInputStyle.Short, "Причина бана", required: false, maxLength: 200)
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "mod_mute":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await component.RespondAsync("Сначала выберите участника в выпадающем списке.", ephemeral: true);
                    return;
                }

                if (await _punishmentService.HasActiveTimedMuteAsync(eventId, targetUserId))
                {
                    await component.DeferAsync(ephemeral: true);
                    var (_, msg) = await _punishmentService.RemoveTimedMuteAsync(eventId, actor.Id, targetUserId);
                    await component.FollowupAsync(msg, ephemeral: true);
                    return;
                }

                var modal = new ModalBuilder()
                    .WithTitle("Временный мут участника")
                    .WithCustomId($"ev:{eventId}:modal_mute:{targetUserId}")
                    .AddTextInput("Длительность (например: 30m, 2h, 1d)", "duration", TextInputStyle.Short, "Минимум 1m", 1, 10, true, "30m")
                    .AddTextInput("Причина (необязательно)", "reason", TextInputStyle.Short, "Причина мута", required: false, maxLength: 200)
                    .Build();

                await component.RespondWithModalAsync(modal);
                return;
            }

            case "mod_mic":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await component.RespondAsync("Сначала выберите участника в выпадающем списке.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);
                var (_, message, _) = await _punishmentService.ToggleVoiceMicMuteAsync(eventId, actor, targetUserId);
                await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "mod_warn":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await component.RespondAsync("Сначала выберите участника в выпадающем списке.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);
                var (_, message) = await _punishmentService.AddWarningAsync(eventId, actor, targetUserId);
                await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "mod_kick":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(component, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await component.RespondAsync("Сначала выберите участника в выпадающем списке.", ephemeral: true);
                    return;
                }

                await component.DeferAsync(ephemeral: true);
                var (_, message) = await _punishmentService.KickUserAsync(eventId, actor, targetUserId, "Кик через панель управления");
                await component.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "log_page":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var logTypeKey = arg1;
                if (!int.TryParse(arg2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
                    page = 0;

                var (embed, components) = await _logBuilder.BuildLogPageAsync(actor.Guild, actor, logTypeKey, page);
                await component.UpdateAsync(msg =>
                {
                    msg.Embed = embed;
                    msg.Components = components;
                });
                return;
            }

            case "hist_page":
            {
                if (component.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await component.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId))
                    targetUserId = actor.Id;

                if (!int.TryParse(arg2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
                    page = 0;

                if (targetUserId != actor.Id &&
                    !_hierarchyService.IsDeveloper(actor) &&
                    !_hierarchyService.CanControl(actor.Guild, actor.Id, targetUserId))
                {
                    await component.RespondAsync("Недостаточно прав для просмотра чужой истории.", ephemeral: true);
                    return;
                }

                var (embed, components) = await _logBuilder.BuildUserHistoryPageAsync(actor.Guild.Id, targetUserId, page);
                await component.UpdateAsync(msg =>
                {
                    msg.Embed = embed;
                    msg.Components = components;
                });
                return;
            }

            default:
                await component.RespondAsync("Неизвестное действие кнопки.", ephemeral: true);
                return;
        }
    }

    private async Task HandleModalAsync(SocketModal modal)
    {
        var customId = modal.Data.CustomId;
        var parts = customId.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            await modal.RespondAsync("Некорректный идентификатор формы.", ephemeral: true);
            return;
        }

        var action = parts[2];
        var arg1 = parts.Length > 3 ? parts[3] : string.Empty;

        string GetField(string fieldId) =>
            modal.Data.Components.FirstOrDefault(c => string.Equals(c.CustomId, fieldId, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        switch (action)
        {
            case "modal_custom":
            {
                if (modal.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await modal.RespondAsync("Недостаточно прав для запуска ивента.", ephemeral: true);
                    return;
                }

                var name = GetField("name");
                var description = GetField("description");
                var maxRaw = GetField("max_participants");

                var limits = Settings.Event.Limits;
                var maxParticipants = string.IsNullOrWhiteSpace(maxRaw)
                    ? limits.DefaultParticipants
                    : int.TryParse(maxRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : -1;

                if (maxParticipants < limits.MinParticipants || maxParticipants > limits.MaxParticipants)
                {
                    await modal.RespondAsync($"Максимум участников должен быть числом от {limits.MinParticipants} до {limits.MaxParticipants}.", ephemeral: true);
                    return;
                }

                await modal.DeferAsync(ephemeral: true);
                var (success, message, _) = await _eventService.CreateCustomEventAsync(
                    actor.Guild, actor, name, description, maxParticipants);

                await modal.FollowupAsync(success ? $"✅ {message}" : $"❌ {message}", ephemeral: true);
                return;
            }

            case "modal_room_create":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId);
                if (!allowed || actor is null)
                    return;

                var roomName = GetField("name");
                var maxRaw = GetField("max_participants");

                if (!int.TryParse(maxRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxParticipants) || maxParticipants is < 1 or > 99)
                {
                    await modal.RespondAsync("Количество участников комнаты должно быть числом от 1 до 99.", ephemeral: true);
                    return;
                }

                await modal.DeferAsync(ephemeral: true);
                var (success, message, _) = await _eventService.CreateAdditionalRoomAsync(eventId, actor, roomName, maxParticipants);
                await modal.FollowupAsync(success ? $"✅ {message}" : $"❌ {message}", ephemeral: true);
                return;
            }

            case "modal_room_edit":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId);
                if (!allowed || actor is null)
                    return;

                if (!int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId))
                {
                    await modal.RespondAsync("Комната не найдена.", ephemeral: true);
                    return;
                }

                var roomName = GetField("name");
                var maxRaw = GetField("max_participants");

                if (!int.TryParse(maxRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxParticipants) || maxParticipants is < 1 or > 99)
                {
                    await modal.RespondAsync("Количество участников комнаты должно быть числом от 1 до 99.", ephemeral: true);
                    return;
                }

                await modal.DeferAsync(ephemeral: true);
                var (success, message, _) = await _eventService.EditAdditionalRoomAsync(eventId, roomId, actor, roomName, maxParticipants);
                await modal.FollowupAsync(success ? $"✅ {message}" : $"❌ {message}", ephemeral: true);
                return;
            }

            case "modal_settings_add":
            {
                if (modal.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await modal.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var categoryKey = string.IsNullOrWhiteSpace(arg1) ? "board" : arg1;
                var name = GetField("name");
                var description = GetField("description");
                var maxRaw = GetField("max_participants");

                if (!int.TryParse(maxRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxParticipants))
                {
                    await modal.RespondAsync("Количество участников должно быть числом от 4 до 99.", ephemeral: true);
                    return;
                }

                var (success, message, _) = await _gamePresetsService.AddGamePresetAsync(
                    categoryKey, name, description, maxParticipants);

                await modal.RespondAsync(success ? $"✅ {message}" : $"❌ {message}", ephemeral: true);
                return;
            }

            case "modal_settings_edit":
            {
                if (modal.User is not SocketGuildUser actor || !_hierarchyService.HasEventAccess(actor))
                {
                    await modal.RespondAsync("Недостаточно прав.", ephemeral: true);
                    return;
                }

                var presetId = arg1;
                var name = GetField("name");
                var description = GetField("description");
                var maxRaw = GetField("max_participants");

                if (!int.TryParse(maxRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxParticipants))
                {
                    await modal.RespondAsync("Количество участников должно быть числом от 4 до 99.", ephemeral: true);
                    return;
                }

                var (success, message, _) = await _gamePresetsService.EditGamePresetAsync(
                    presetId, name, description, maxParticipants);

                await modal.RespondAsync(success ? $"✅ {message}" : $"❌ {message}", ephemeral: true);
                return;
            }

            case "modal_instruction":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId);
                if (!allowed || actor is null)
                    return;

                var link = GetField("link");
                var code = GetField("code");
                var instructionText = GetField("instruction_text");

                await modal.DeferAsync(ephemeral: true);
                var (success, message) = await _eventService.UpdateInstructionAsync(eventId, actor, link, code, instructionText);
                await modal.FollowupAsync(success ? $"✅ {message}" : $"❌ {message}", ephemeral: true);
                return;
            }

            case "modal_limit":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId);
                if (!allowed || actor is null)
                    return;

                var maxRaw = GetField("max_participants");
                if (!int.TryParse(maxRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var newLimit))
                {
                    await modal.RespondAsync("Введите корректное число от 4 до 99.", ephemeral: true);
                    return;
                }

                await modal.DeferAsync(ephemeral: true);
                var (_, message, _) = await _eventService.UpdateMaxParticipantsAsync(eventId, actor, newLimit);
                await modal.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "modal_name":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId);
                if (!allowed || actor is null)
                    return;

                var newName = GetField("name");
                await modal.DeferAsync(ephemeral: true);
                var (_, message, _) = await _eventService.UpdateNameAsync(eventId, actor, newName);
                await modal.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "modal_desc":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId);
                if (!allowed || actor is null)
                    return;

                var newDesc = GetField("description");
                await modal.DeferAsync(ephemeral: true);
                var (_, message, _) = await _eventService.UpdateDescriptionAsync(eventId, actor, newDesc);
                await modal.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "modal_redact":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId);
                if (!allowed || actor is null)
                    return;

                var newName = GetField("name");
                var newDesc = GetField("description");
                var maxRaw = GetField("max_participants");

                if (!int.TryParse(maxRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var newLimit))
                {
                    await modal.RespondAsync("Максимум участников должен быть числом от 4 до 99.", ephemeral: true);
                    return;
                }

                await modal.DeferAsync(ephemeral: true);
                var (_, message) = await _eventService.RedactEventAsync(eventId, actor, newName, newDesc, newLimit);
                await modal.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "modal_ban":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await modal.RespondAsync("Участник не выбран.", ephemeral: true);
                    return;
                }

                var durationRaw = GetField("duration");
                var reason = GetField("reason");

                if (!PunishmentService.TryParseDuration(durationRaw, out var duration, out var durationError))
                {
                    await modal.RespondAsync(durationError, ephemeral: true);
                    return;
                }

                await modal.DeferAsync(ephemeral: true);
                var (_, message) = await _punishmentService.BanUserAsync(eventId, actor, targetUserId, reason, duration);
                await modal.FollowupAsync(message, ephemeral: true);
                return;
            }

            case "modal_mute":
            {
                var (allowed, _, actor, _) = await ValidateManagementAccessAsync(modal, eventId, requireDirectHostOrDev: true);
                if (!allowed || actor is null)
                    return;

                if (!ulong.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetUserId) || targetUserId == 0)
                {
                    await modal.RespondAsync("Участник не выбран.", ephemeral: true);
                    return;
                }

                var durationRaw = GetField("duration");
                var reason = GetField("reason");

                if (!PunishmentService.TryParseDuration(durationRaw, out var duration, out var durationError))
                {
                    await modal.RespondAsync(durationError, ephemeral: true);
                    return;
                }

                await modal.DeferAsync(ephemeral: true);
                var (_, message) = await _punishmentService.ApplyTimedMuteAsync(eventId, actor, targetUserId, duration, reason);
                await modal.FollowupAsync(message, ephemeral: true);
                return;
            }

            default:
                await modal.RespondAsync("Неизвестная форма.", ephemeral: true);
                return;
        }
    }
}
