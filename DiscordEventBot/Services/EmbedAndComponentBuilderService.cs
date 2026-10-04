using System.Globalization;
using Discord;
using Discord.WebSocket;
using DiscordEventBot.Data;

namespace DiscordEventBot.Services;

public sealed class EmbedAndComponentBuilderService
{
    public const int ParticipantsPageSize = 25;
    private static readonly Color DarkPanelColor = new(43, 45, 49);

    private readonly DiscordSocketClient _client;

    public EmbedAndComponentBuilderService(DiscordSocketClient client)
    {
        _client = client;
    }

    public (IEmote Emote, string MentionText) ResolveCustomEmote(SocketGuild? guild, string emojiName, string fallbackUnicode)
    {
        var cleanName = emojiName.Trim(':');

        var guildEmote = guild?.Emotes.FirstOrDefault(e =>
            string.Equals(e.Name, cleanName, StringComparison.OrdinalIgnoreCase));

        guildEmote ??= _client.Guilds
            .SelectMany(g => g.Emotes)
            .FirstOrDefault(e => string.Equals(e.Name, cleanName, StringComparison.OrdinalIgnoreCase));

        if (guildEmote is not null)
        {
            return (guildEmote, guildEmote.ToString());
        }

        return (new Emoji(fallbackUnicode), fallbackUnicode);
    }

    public static Color ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return DarkPanelColor;

        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 && uint.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return new Color(rgb);
        }

        return DarkPanelColor;
    }

    public static string FormatCategoryName(string categoryKey) => categoryKey.ToLowerInvariant() switch
    {
        "board" => "Настольные игры",
        "roleplay" => "Ролевые игры",
        "entertainment" => "Развлекательные игры",
        "multiplayer" => "Мультиплеерные игры",
        "custom" => "Кастомная игра",
        _ => categoryKey
    };

    public Embed BuildManagementMainEmbed(
        EventEntity ev,
        int participantsCount,
        int inVoiceCount,
        int activeBansCount,
        int kicksCount,
        int warningsCount,
        SocketGuild? guild = null)
    {
        guild ??= _client.GetGuild(ev.GuildId);

        var (_, lockTxt) = ResolveCustomEmote(guild, "Frame1", "🔒");
        var (_, muteAllTxt) = ResolveCustomEmote(guild, "Frame6", "🔇");
        var (_, deafAllTxt) = ResolveCustomEmote(guild, "Frame9", "🔕");
        var (_, editTxt) = ResolveCustomEmote(guild, "Frame4", "✏️");
        var (_, instrTxt) = ResolveCustomEmote(guild, "Frame3", "📖");
        var (_, gatherTxt) = ResolveCustomEmote(guild, "Frame10", "🧲");
        var (_, ownerTxt) = ResolveCustomEmote(guild, "giveOwnertealdimensional512", "👑");
        var (_, alarmTxt) = ResolveCustomEmote(guild, "alarm", "🔔");
        var (_, infoTxt) = ResolveCustomEmote(guild, "creative", "💡");
        var (_, peopleTxt) = ResolveCustomEmote(guild, "peoplegroupteal", "👥");

        var legendLeft =
            $"{lockTxt} — закрыть / открыть комнату\n" +
            $"{muteAllTxt} — замьютить / размьютить всех\n" +
            $"{deafAllTxt} — отключить / включить звуки\n" +
            $"{editTxt} — редактировать (название, описание, лимит)\n" +
            $"{instrTxt} — инструкция по заходу";

        var legendRight =
            $"{gatherTxt} — созыв в основную руму\n" +
            $"{ownerTxt} — назначить ведущего\n" +
            $"{alarmTxt} — уведомить всех об ивенте\n" +
            $"{infoTxt} — информация об ивенте\n" +
            "🛑 — завершить ивент";

        var voiceState = ev.IsVoiceClosed ? $"{lockTxt} Закрыт" : "🔓 Открыт";
        var muteState = ev.IsAllMuted ? $"{muteAllTxt} Все замьючены" : "🔊 Микрофоны открыты";
        var deafState = ev.IsAllDeafened ? $" • {deafAllTxt} Звук выкл." : string.Empty;
        var hasInstruction = !string.IsNullOrWhiteSpace(ev.InstructionLink) ||
                             !string.IsNullOrWhiteSpace(ev.InstructionCode) ||
                             !string.IsNullOrWhiteSpace(ev.InstructionText);
        var instrState = hasInstruction ? $"{instrTxt} Инструкция добавлена" : $"{instrTxt} Без инструкции";

        var builder = new EmbedBuilder()
            .WithTitle($"Управление ивентом — {ev.Name}")
            .WithColor(DarkPanelColor)
            .AddField("\u200B", legendLeft, inline: true)
            .AddField("\u200B", legendRight, inline: true)
            .AddField(
                "Статус",
                $"Ведущий: <@{ev.HostUserId}> • {peopleTxt} Участники: **{participantsCount} / {ev.MaxParticipants}** • {voiceState} • {muteState}{deafState} • {instrState}",
                inline: false);

        return builder.Build();
    }

    public (Embed Embed, MessageComponent Components) BuildRoomPanelMessage(
        EventEntity ev,
        EventRoomEntity room,
        SocketGuild? guild = null)
    {
        guild ??= _client.GetGuild(ev.GuildId);

        var (lockEmote, lockTxt) = ResolveCustomEmote(guild, "Frame1", room.IsVoiceClosed ? "🔓" : "🔒");
        var (muteAllEmote, muteAllTxt) = ResolveCustomEmote(guild, "Frame6", "🔇");
        var (editEmote, editTxt) = ResolveCustomEmote(guild, "Frame4", "✏️");
        var (infoEmote, infoTxt) = ResolveCustomEmote(guild, "creative", "💡");
        var (_, peopleTxt) = ResolveCustomEmote(guild, "peoplegroupteal", "👥");

        var voiceChannel = guild?.GetVoiceChannel(room.VoiceChannelId);
        var inRoomNow = voiceChannel?.ConnectedUsers.Count(u => !u.IsBot) ?? 0;

        var legendLeft =
            $"{lockTxt} — закрыть / открыть комнату\n" +
            $"{muteAllTxt} — замьютить / размьютить всех";

        var legendRight =
            $"{editTxt} — изменить название и лимит\n" +
            $"{infoTxt} — информация о комнате";

        var voiceState = room.IsVoiceClosed ? $"{lockTxt} Закрыта" : "🔓 Открыта";
        var muteState = room.IsAllMuted ? $"{muteAllTxt} Все замьючены" : "🔊 Микрофоны открыты";

        var embed = new EmbedBuilder()
            .WithTitle($"Панель управления комнатой — {room.Name}")
            .WithColor(DarkPanelColor)
            .AddField("\u200B", legendLeft, inline: true)
            .AddField("\u200B", legendRight, inline: true)
            .AddField(
                "Статус комнаты",
                $"Канал: <#{room.VoiceChannelId}> • {peopleTxt} Лимит: **{inRoomNow} / {room.MaxParticipants}** • {voiceState} • {muteState}",
                inline: false)
            .Build();

        var voiceStyle = room.IsVoiceClosed ? ButtonStyle.Success : ButtonStyle.Secondary;
        var muteStyle = room.IsAllMuted ? ButtonStyle.Danger : ButtonStyle.Secondary;

        var row = new ActionRowBuilder()
            .WithButton(label: null, customId: $"ev:{ev.Id}:room_panel_voice:{room.Id}", style: voiceStyle, emote: lockEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:room_panel_mute:{room.Id}", style: muteStyle, emote: muteAllEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:room_panel_edit:{room.Id}", style: ButtonStyle.Secondary, emote: editEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:room_panel_info:{room.Id}", style: ButtonStyle.Secondary, emote: infoEmote);

        var components = new ComponentBuilder()
            .AddRow(row)
            .Build();

        return (embed, components);
    }

    public Embed BuildRoomDetailedInfoEmbed(
        EventEntity ev,
        EventRoomEntity room,
        SocketGuild? guild = null)
    {
        guild ??= _client.GetGuild(ev.GuildId);
        var voiceChannel = guild?.GetVoiceChannel(room.VoiceChannelId);
        var connectedMembers = voiceChannel?.ConnectedUsers.Where(u => !u.IsBot).ToList() ?? new List<SocketGuildUser>();

        var membersMentions = connectedMembers.Count > 0
            ? string.Join(", ", connectedMembers.Select(u => $"<@{u.Id}>"))
            : "В комнате сейчас никого нет";

        if (membersMentions.Length > 1020)
            membersMentions = membersMentions[..1020] + "...";

        var voiceStatusText = room.IsVoiceClosed ? "🔒 Закрыта" : "🔓 Открыта";
        var allMutedText = room.IsAllMuted ? "🔇 Включён" : "🔊 Выключен";

        return new EmbedBuilder()
            .WithTitle($"🔊 Комната: {room.Name}")
            .WithColor(DarkPanelColor)
            .AddField("Ивент", ev.Name, inline: true)
            .AddField("Канал", $"<#{room.VoiceChannelId}>", inline: true)
            .AddField("Лимит участников", $"{connectedMembers.Count} / {room.MaxParticipants}", inline: true)
            .AddField("Статус входа", voiceStatusText, inline: true)
            .AddField("Общий мут комнаты", allMutedText, inline: true)
            .AddField("Участники в комнате", membersMentions, inline: false)
            .Build();
    }

    public Embed BuildDetailedInfoPopupEmbed(
        EventEntity ev,
        IReadOnlyCollection<ParticipantEntity> participants,
        int activeBansCount,
        int kicksCount,
        int warningsCount)
    {
        var desc = string.IsNullOrWhiteSpace(ev.Description) ? "_Описание не указано_" : ev.Description;
        var voiceStatusText = ev.IsVoiceClosed ? "🔒 Закрыт" : "🔓 Открыт";
        var allMutedText = ev.IsAllMuted ? "🔇 Включён" : "🔊 Выключен";

        var participantsMentions = participants.Count > 0
            ? string.Join(", ", participants.OrderBy(p => p.JoinedAtUtc).Select(p => $"<@{p.UserId}>"))
            : "Нет участников";

        if (participantsMentions.Length > 1020)
            participantsMentions = participantsMentions[..1020] + "...";

        return new EmbedBuilder()
            .WithTitle($"{ev.Emoji} {ev.Name}")
            .WithDescription(desc)
            .WithColor(DarkPanelColor)
            .AddField("Категория", FormatCategoryName(ev.CategoryKey), inline: true)
            .AddField("Ведущий", $"<@{ev.HostUserId}>", inline: true)
            .AddField("Участники (счётчик)", $"{participants.Count} / {ev.MaxParticipants}", inline: true)
            .AddField("Голосовой канал", voiceStatusText, inline: true)
            .AddField("Общий мут", allMutedText, inline: true)
            .AddField("Модерация", $"Банов: {activeBansCount} • Киков: {kicksCount} • Предупреждений: {warningsCount}", inline: false)
            .AddField("Участники", participantsMentions, inline: false)
            .Build();
    }

    public Embed BuildParticipantInstructionEmbed(EventEntity ev)
    {
        var hasAny = !string.IsNullOrWhiteSpace(ev.InstructionLink) ||
                     !string.IsNullOrWhiteSpace(ev.InstructionCode) ||
                     !string.IsNullOrWhiteSpace(ev.InstructionText);

        var builder = new EmbedBuilder()
            .WithTitle($"📖 Инструкция по заходу — {ev.Name}")
            .WithColor(ParseHexColor(ev.ColorHex));

        if (!hasAny)
        {
            builder.WithDescription("Ведущий ещё не добавил ссылку, код или инструкцию по заходу для этого ивента.");
            return builder.Build();
        }

        if (!string.IsNullOrWhiteSpace(ev.InstructionText))
        {
            builder.WithDescription(ev.InstructionText);
        }

        if (!string.IsNullOrWhiteSpace(ev.InstructionLink))
        {
            builder.AddField("🔗 Ссылка", ev.InstructionLink, inline: true);
        }

        if (!string.IsNullOrWhiteSpace(ev.InstructionCode))
        {
            builder.AddField("🔑 Код", $"`{ev.InstructionCode}`", inline: true);
        }

        builder.AddField("Ведущий", $"<@{ev.HostUserId}>", inline: true);
        return builder.Build();
    }

    public (Embed Embed, MessageComponent Components) BuildClosedRoomJoinRequestMessage(int eventId, IGuildUser user)
    {
        var joinedText = user.JoinedAt.HasValue
            ? $"<t:{user.JoinedAt.Value.ToUnixTimeSeconds()}:D> (<t:{user.JoinedAt.Value.ToUnixTimeSeconds()}:R>)"
            : "Неизвестно";

        var embed = new EmbedBuilder()
            .WithTitle("Заявка на участие в закрытой комнате")
            .WithColor(DarkPanelColor)
            .AddField("Ник", $"{user.Mention} (`{user.Username}`)", inline: true)
            .AddField("ID", $"`{user.Id}`", inline: true)
            .AddField("Дата захода на сервер", joinedText, inline: false)
            .Build();

        var components = new ComponentBuilder()
            .WithButton("Одобрить", $"ev:{eventId}:join_approve:{user.Id}", ButtonStyle.Success)
            .WithButton("Отклонить", $"ev:{eventId}:join_reject:{user.Id}", ButtonStyle.Danger)
            .Build();

        return (embed, components);
    }

    public (Embed Embed, MessageComponent Components) BuildSubRoomManagementMessage(EventRoomEntity room)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"Управление комнатой — {room.Name}")
            .WithDescription($"Канал: <#{room.VoiceChannelId}>\nЛимит участников: **{room.MaxParticipants}**")
            .WithColor(DarkPanelColor)
            .Build();

        var components = new ComponentBuilder()
            .WithButton("Удалить", $"ev:{room.EventId}:room_delete:{room.Id}", ButtonStyle.Danger, new Emoji("🗑️"))
            .WithButton("Редактировать", $"ev:{room.EventId}:room_edit:{room.Id}", ButtonStyle.Primary, new Emoji("✏️"))
            .Build();

        return (embed, components);
    }

    public MessageComponent BuildManagementMainComponents(EventEntity ev, SocketGuild? guild = null)
    {
        guild ??= _client.GetGuild(ev.GuildId);

        var (lockEmote, _) = ResolveCustomEmote(guild, "Frame1", ev.IsVoiceClosed ? "🔓" : "🔒");
        var (muteAllEmote, _) = ResolveCustomEmote(guild, "Frame6", "🔇");
        var (deafAllEmote, _) = ResolveCustomEmote(guild, "Frame9", "🔕");
        var (editEmote, _) = ResolveCustomEmote(guild, "Frame4", "✏️");
        var (instrEmote, _) = ResolveCustomEmote(guild, "Frame3", "📖");
        var (gatherEmote, _) = ResolveCustomEmote(guild, "Frame10", "🧲");
        var (ownerEmote, _) = ResolveCustomEmote(guild, "giveOwnertealdimensional512", "👑");
        var (alarmEmote, _) = ResolveCustomEmote(guild, "alarm", "🔔");
        var (infoEmote, _) = ResolveCustomEmote(guild, "creative", "💡");

        var voiceStyle = ev.IsVoiceClosed ? ButtonStyle.Success : ButtonStyle.Secondary;
        var muteStyle = ev.IsAllMuted ? ButtonStyle.Danger : ButtonStyle.Secondary;
        var deafStyle = ev.IsAllDeafened ? ButtonStyle.Danger : ButtonStyle.Secondary;

        var hasInstruction = !string.IsNullOrWhiteSpace(ev.InstructionLink) ||
                             !string.IsNullOrWhiteSpace(ev.InstructionCode) ||
                             !string.IsNullOrWhiteSpace(ev.InstructionText);
        var instrStyle = hasInstruction ? ButtonStyle.Primary : ButtonStyle.Secondary;

        var row1 = new ActionRowBuilder()
            .WithButton(label: null, customId: $"ev:{ev.Id}:toggle_voice", style: voiceStyle, emote: lockEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:mute_all", style: muteStyle, emote: muteAllEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:deafen_all", style: deafStyle, emote: deafAllEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:edit_combined", style: ButtonStyle.Secondary, emote: editEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:edit_instruction", style: instrStyle, emote: instrEmote);

        var row2 = new ActionRowBuilder()
            .WithButton(label: null, customId: $"ev:{ev.Id}:gather_voice", style: ButtonStyle.Secondary, emote: gatherEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:transfer_prompt", style: ButtonStyle.Secondary, emote: ownerEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:notify_ping", style: ButtonStyle.Secondary, emote: alarmEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:info_popup", style: ButtonStyle.Secondary, emote: infoEmote)
            .WithButton(label: null, customId: $"ev:{ev.Id}:close_prompt", style: ButtonStyle.Danger, emote: new Emoji("🛑"));

        return new ComponentBuilder()
            .AddRow(row1)
            .AddRow(row2)
            .Build();
    }

    public (Embed Embed, MessageComponent Components) BuildModerationMessage(
        SocketGuild? guild,
        EventEntity ev,
        IReadOnlyList<ParticipantEntity> participants,
        IReadOnlyList<PunishmentEntity> activePunishments,
        IReadOnlyList<WarningEntity> warnings)
    {
        guild ??= _client.GetGuild(ev.GuildId);

        var (banEmote, banTxt) = ResolveCustomEmote(guild, "Frame2", "🚫");
        var (muteTimeEmote, muteTimeTxt) = ResolveCustomEmote(guild, "alarm", "⏳");
        var (micEmote, micTxt) = ResolveCustomEmote(guild, "tribune", "🎙");
        var (warnEmote, warnTxt) = ResolveCustomEmote(guild, "warningtealredrawnmax", "⚠️");
        var (kickEmote, kickTxt) = ResolveCustomEmote(guild, "Frame7", "✂️");
        var (_, peopleTxt) = ResolveCustomEmote(guild, "peoplegroupteal", "👥");

        var totalParticipants = participants.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalParticipants / (double)ParticipantsPageSize));
        var currentPage = Math.Clamp(ev.ModerationPage, 0, totalPages - 1);

        var pageItems = participants
            .OrderBy(p => p.JoinedAtUtc)
            .Skip(currentPage * ParticipantsPageSize)
            .Take(ParticipantsPageSize)
            .ToList();

        var selectedUserId = ev.SelectedParticipantUserId;
        var isSelectedBanned = selectedUserId.HasValue && activePunishments.Any(p =>
            p.TargetUserId == selectedUserId.Value && p.Type == PunishmentType.Ban && p.IsActive);
        var activeTimedMute = selectedUserId.HasValue
            ? activePunishments.FirstOrDefault(p =>
                p.TargetUserId == selectedUserId.Value && p.Type == PunishmentType.TimedMute && p.IsActive)
            : null;
        var isSelectedMicMuted = selectedUserId.HasValue && activePunishments.Any(p =>
            p.TargetUserId == selectedUserId.Value && p.Type == PunishmentType.VoiceMicMute && p.IsActive);
        var selectedWarningsCount = selectedUserId.HasValue
            ? warnings.Count(w => w.TargetUserId == selectedUserId.Value)
            : 0;
        var isSelectedInParticipantList = selectedUserId.HasValue && participants.Any(p => p.UserId == selectedUserId.Value);

        var legendLeft =
            $"{banTxt} — бан по времени / разбан\n" +
            $"{muteTimeTxt} — мут по времени / размут\n" +
            $"{micTxt} — выкл. / вкл. микрофон";

        var legendRight =
            $"{warnTxt} — предупреждение (3 = мут 2ч)\n" +
            $"{kickTxt} — выгнать (кикнуть) с ивента";

        var embedBuilder = new EmbedBuilder()
            .WithTitle($"Модерация участников {peopleTxt}")
            .WithColor(DarkPanelColor)
            .AddField("\u200B", legendLeft, inline: true)
            .AddField("\u200B", legendRight, inline: true);

        if (selectedUserId.HasValue)
        {
            var states = new List<string>();
            if (isSelectedBanned) states.Add($"{banTxt} Забанен");
            if (activeTimedMute is not null) states.Add($"{muteTimeTxt} Временный мут");
            if (isSelectedMicMuted) states.Add($"{micTxt} Микрофон выкл.");
            if (selectedWarningsCount > 0) states.Add($"{warnTxt} Предупреждений: {selectedWarningsCount}/3");
            var stateSummary = states.Count > 0 ? string.Join(" • ", states) : "Активных ограничений нет";

            embedBuilder.AddField("Выбранный участник", $"<@{selectedUserId.Value}> — {stateSummary}", inline: false);
        }

        var componentBuilder = new ComponentBuilder();

        var selectMenu = new SelectMenuBuilder()
            .WithCustomId($"ev:{ev.Id}:select_part:{currentPage}")
            .WithPlaceholder(totalParticipants > 0
                ? $"Выберите участника ({totalParticipants})"
                : "Список участников пуст")
            .WithMinValues(1)
            .WithMaxValues(1);

        if (pageItems.Count == 0)
        {
            selectMenu.AddOption("Участников пока нет", "none", "Никто ещё не записался на ивент");
            selectMenu.WithDisabled(true);
        }
        else
        {
            foreach (var part in pageItems)
            {
                var guildUser = guild?.GetUser(part.UserId);
                var displayName = guildUser?.DisplayName ?? guildUser?.Username ?? $"Участник {part.UserId}";
                if (displayName.Length > 80)
                    displayName = displayName[..80];

                var userWarns = warnings.Count(w => w.TargetUserId == part.UserId);
                var userMuted = activePunishments.Any(p =>
                    p.TargetUserId == part.UserId &&
                    (p.Type == PunishmentType.TimedMute || p.Type == PunishmentType.VoiceMicMute) &&
                    p.IsActive);

                var tags = new List<string>();
                if (userMuted) tags.Add("🔇 Мут");
                if (userWarns > 0) tags.Add($"⚠️ {userWarns}/3");
                var descText = tags.Count > 0 ? string.Join(" • ", tags) : $"ID: {part.UserId}";

                selectMenu.AddOption(
                    label: displayName,
                    value: part.UserId.ToString(CultureInfo.InvariantCulture),
                    description: descText,
                    isDefault: selectedUserId.HasValue && selectedUserId.Value == part.UserId);
            }
        }

        componentBuilder.WithSelectMenu(selectMenu, row: 0);

        var targetArg = selectedUserId?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var hasTarget = selectedUserId.HasValue && selectedUserId.Value != 0;

        var banStyle = isSelectedBanned ? ButtonStyle.Success : ButtonStyle.Secondary;
        var muteStyle = activeTimedMute is not null ? ButtonStyle.Success : ButtonStyle.Secondary;
        var micStyle = isSelectedMicMuted ? ButtonStyle.Success : ButtonStyle.Secondary;

        var modButtonsRow = new ActionRowBuilder()
            .WithButton(label: null, customId: $"ev:{ev.Id}:mod_ban:{targetArg}", style: banStyle, emote: banEmote, disabled: !hasTarget)
            .WithButton(label: null, customId: $"ev:{ev.Id}:mod_mute:{targetArg}", style: muteStyle, emote: muteTimeEmote, disabled: !hasTarget)
            .WithButton(label: null, customId: $"ev:{ev.Id}:mod_mic:{targetArg}", style: micStyle, emote: micEmote, disabled: !hasTarget)
            .WithButton(label: null, customId: $"ev:{ev.Id}:mod_warn:{targetArg}", style: ButtonStyle.Secondary, emote: warnEmote, disabled: !hasTarget || !isSelectedInParticipantList)
            .WithButton(label: null, customId: $"ev:{ev.Id}:mod_kick:{targetArg}", style: ButtonStyle.Danger, emote: kickEmote, disabled: !hasTarget || !isSelectedInParticipantList);

        componentBuilder.AddRow(modButtonsRow);

        if (totalPages > 1)
        {
            var prevDisabled = currentPage <= 0;
            var nextDisabled = currentPage >= totalPages - 1;
            var paginationRow = new ActionRowBuilder()
                .WithButton("◀", $"ev:{ev.Id}:mod_page:{Math.Max(0, currentPage - 1)}", ButtonStyle.Secondary, disabled: prevDisabled)
                .WithButton("▶", $"ev:{ev.Id}:mod_page:{Math.Min(totalPages - 1, currentPage + 1)}", ButtonStyle.Secondary, disabled: nextDisabled);

            componentBuilder.AddRow(paginationRow);
        }

        return (embedBuilder.Build(), componentBuilder.Build());
    }

    public (Embed Embed, MessageComponent Components) BuildInfoChannelMessage(
        EventEntity ev,
        int participantsCount,
        int inVoiceCount)
    {
        var guild = _client.GetGuild(ev.GuildId);
        var (_, peopleTxt) = ResolveCustomEmote(guild, "peoplegroupteal", "👥");
        var (instrEmote, _) = ResolveCustomEmote(guild, "creative", "📖");

        var desc = string.IsNullOrWhiteSpace(ev.Description)
            ? "Нажмите кнопку **«Участвовать»** ниже или заходите в голосовой канал ивента."
            : ev.Description;

        var voiceStatusText = ev.IsVoiceClosed ? "🔒 Закрыт" : "🔓 Открыт";

        var embedBuilder = new EmbedBuilder()
            .WithTitle($"{ev.Emoji} {ev.Name}")
            .WithDescription(desc)
            .WithColor(ParseHexColor(ev.ColorHex))
            .AddField("Ведущий", $"<@{ev.HostUserId}>", inline: true)
            .AddField($"Участники {peopleTxt}", $"**{participantsCount} / {ev.MaxParticipants}**", inline: true)
            .AddField("Голосовой канал", $"<#{ev.VoiceChannelId}> ({voiceStatusText})", inline: true);

        if (!string.IsNullOrWhiteSpace(ev.IconUrl) && Uri.IsWellFormedUriString(ev.IconUrl, UriKind.Absolute))
        {
            embedBuilder.WithThumbnailUrl(ev.IconUrl);
        }

        var isFullAndOpen = !ev.IsVoiceClosed && participantsCount >= ev.MaxParticipants;
        var joinLabel = isFullAndOpen ? $"Мест нет ({participantsCount}/{ev.MaxParticipants})" : "Участвовать";

        var row = new ActionRowBuilder()
            .WithButton(
                label: joinLabel,
                customId: $"ev:{ev.Id}:join",
                style: isFullAndOpen ? ButtonStyle.Secondary : ButtonStyle.Success,
                emote: new Emoji("✅"),
                disabled: isFullAndOpen)
            .WithButton(
                label: "Инструкция",
                customId: $"ev:{ev.Id}:show_instruction",
                style: ButtonStyle.Secondary,
                emote: instrEmote);

        var components = new ComponentBuilder()
            .AddRow(row)
            .Build();

        return (embedBuilder.Build(), components);
    }

    public (Embed Embed, MessageComponent Components) BuildGameSettingsMainMenu(int totalPresetsCount)
    {
        var embed = new EmbedBuilder()
            .WithTitle("⚙️ Управление играми")
            .WithDescription(
                "Выберите действие ниже:\n\n" +
                "➕ **Добавить игру** — выбрать категорию и добавить новую игру.\n" +
                "✏️ **Редактировать игру** — изменить название, описание или лимит участников.\n\n" +
                $"Всего игр: **{totalPresetsCount}**")
            .WithColor(DarkPanelColor)
            .Build();

        var components = new ComponentBuilder()
            .WithButton("Добавить игру", "ev:0:settings_add_btn", ButtonStyle.Success, new Emoji("➕"))
            .WithButton("Редактировать игру", "ev:0:settings_edit_btn", ButtonStyle.Primary, new Emoji("✏️"))
            .Build();

        return (embed, components);
    }
}
