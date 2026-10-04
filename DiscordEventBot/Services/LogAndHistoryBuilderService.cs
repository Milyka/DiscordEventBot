using Discord;
using Discord.WebSocket;
using DiscordEventBot.Data;
using Microsoft.EntityFrameworkCore;

namespace DiscordEventBot.Services;

public sealed class LogAndHistoryBuilderService
{
    public const int PageSize = 10;

    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly HierarchyService _hierarchyService;

    public LogAndHistoryBuilderService(
        IDbContextFactory<BotDbContext> dbFactory,
        HierarchyService hierarchyService)
    {
        _dbFactory = dbFactory;
        _hierarchyService = hierarchyService;
    }

    private bool CanActorSeeHostRecords(SocketGuild guild, IGuildUser actor, ulong hostUserId)
    {
        if (actor.Id == hostUserId || _hierarchyService.IsDeveloper(actor))
            return true;

        return _hierarchyService.CanControl(guild, actor.Id, hostUserId);
    }

    public async Task<(Embed Embed, MessageComponent Components)> BuildLogPageAsync(
        SocketGuild guild,
        IGuildUser actor,
        string logTypeKey,
        int requestedPage)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var lines = new List<string>();
        string title;
        var color = new Color(43, 45, 49);

        switch (logTypeKey)
        {
            case "ban":
            {
                title = "🚫 Журнал банов и разбанов";

                var bans = await db.Punishments
                    .Include(p => p.Event)
                    .AsNoTracking()
                    .Where(p => p.Type == PunishmentType.Ban)
                    .OrderByDescending(p => p.CreatedAtUtc)
                    .Take(250)
                    .ToListAsync();

                foreach (var b in bans)
                {
                    var hostId = b.Event?.HostUserId ?? b.ModeratorUserId;
                    if (!CanActorSeeHostRecords(guild, actor, hostId))
                        continue;

                    var evName = b.Event?.Name ?? $"Ивент #{b.EventId}";
                    var ts = new DateTimeOffset(DateTime.SpecifyKind(b.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    var state = b.IsActive
                        ? "🔴 Активен"
                        : $"🟢 Разбанен (<@{b.RevokedByUserId ?? 0}>)";

                    lines.Add($"• <t:{ts}:d> <t:{ts}:t> | **{evName}** | <@{b.ModeratorUserId}> → <@{b.TargetUserId}> | `{b.Reason}` ({state})");
                }
                break;
            }

            case "kick":
            {
                title = "👢 Журнал киков";

                var kicks = await db.Punishments
                    .Include(p => p.Event)
                    .AsNoTracking()
                    .Where(p => p.Type == PunishmentType.Kick)
                    .OrderByDescending(p => p.CreatedAtUtc)
                    .Take(250)
                    .ToListAsync();

                foreach (var k in kicks)
                {
                    var hostId = k.Event?.HostUserId ?? k.ModeratorUserId;
                    if (!CanActorSeeHostRecords(guild, actor, hostId))
                        continue;

                    var evName = k.Event?.Name ?? $"Ивент #{k.EventId}";
                    var ts = new DateTimeOffset(DateTime.SpecifyKind(k.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    lines.Add($"• <t:{ts}:d> <t:{ts}:t> | **{evName}** | <@{k.ModeratorUserId}> → <@{k.TargetUserId}> | `{k.Reason}`");
                }
                break;
            }

            case "history":
            {
                title = "📜 Завершённые ивенты";

                var history = await db.EventHistory
                    .AsNoTracking()
                    .Where(h => h.GuildId == guild.Id)
                    .OrderByDescending(h => h.EndedAtUtc)
                    .Take(250)
                    .ToListAsync();

                foreach (var h in history)
                {
                    if (!CanActorSeeHostRecords(guild, actor, h.HostUserId))
                        continue;

                    var startTs = new DateTimeOffset(DateTime.SpecifyKind(h.StartedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    var endTs = new DateTimeOffset(DateTime.SpecifyKind(h.EndedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    lines.Add($"• **{h.Name}** | Ведущий: <@{h.HostUserId}> | Участников: `{h.TotalParticipantsCount}` | <t:{startTs}:t> – <t:{endTs}:f>");
                }
                break;
            }

            case "control":
            default:
            {
                title = "🛡 Вмешательства контролёров";

                var controls = await db.ControlLogs
                    .AsNoTracking()
                    .OrderByDescending(c => c.CreatedAtUtc)
                    .Take(250)
                    .ToListAsync();

                foreach (var c in controls)
                {
                    if (!CanActorSeeHostRecords(guild, actor, c.HostUserId) && c.ControllerUserId != actor.Id)
                        continue;

                    var ts = new DateTimeOffset(DateTime.SpecifyKind(c.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    lines.Add($"• <t:{ts}:d> <t:{ts}:t> | <@{c.ControllerUserId}> → **{c.EventName}** (<@{c.HostUserId}>) | {c.ActionDescription}");
                }
                break;
            }
        }

        var totalItems = lines.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)PageSize));
        var currentPage = Math.Clamp(requestedPage, 0, totalPages - 1);

        var pageLines = lines
            .Skip(currentPage * PageSize)
            .Take(PageSize)
            .ToList();

        var description = pageLines.Count > 0
            ? string.Join("\n\n", pageLines)
            : "_Записей пока нет._";

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(description)
            .WithColor(color)
            .Build();

        var compBuilder = new ComponentBuilder();
        if (totalPages > 1)
        {
            compBuilder
                .WithButton("◀", $"ev:0:log_page:{logTypeKey}:{Math.Max(0, currentPage - 1)}", ButtonStyle.Secondary, disabled: currentPage <= 0)
                .WithButton("▶", $"ev:0:log_page:{logTypeKey}:{Math.Min(totalPages - 1, currentPage + 1)}", ButtonStyle.Secondary, disabled: currentPage >= totalPages - 1);
        }

        return (embed, compBuilder.Build());
    }

    public async Task<(Embed Embed, MessageComponent Components)> BuildUserHistoryPageAsync(
        ulong guildId,
        ulong targetUserId,
        int requestedPage)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var activeEvents = await db.Events
            .Include(e => e.Participants)
            .AsNoTracking()
            .Where(e => e.GuildId == guildId && e.HostUserId == targetUserId && e.Status == EventStatus.Active)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ToListAsync();

        var closedHistory = await db.EventHistory
            .AsNoTracking()
            .Where(h => h.GuildId == guildId && h.HostUserId == targetUserId)
            .OrderByDescending(h => h.EndedAtUtc)
            .Take(200)
            .ToListAsync();

        var lines = new List<string>();

        foreach (var act in activeEvents)
        {
            var startTs = new DateTimeOffset(DateTime.SpecifyKind(act.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var catName = EmbedAndComponentBuilderService.FormatCategoryName(act.CategoryKey);
            lines.Add($"🟢 **[АКТИВЕН] {act.Name}** ({catName})\nНачало: <t:{startTs}:f> • Участников: `{act.Participants.Count}/{act.MaxParticipants}`");
        }

        foreach (var item in closedHistory)
        {
            var startTs = new DateTimeOffset(DateTime.SpecifyKind(item.StartedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var endTs = new DateTimeOffset(DateTime.SpecifyKind(item.EndedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var catName = EmbedAndComponentBuilderService.FormatCategoryName(item.CategoryKey);
            lines.Add($"⚪ **[{item.Status}] {item.Name}** ({catName})\nВремя: <t:{startTs}:f> — <t:{endTs}:t> • Участников: `{item.TotalParticipantsCount}`");
        }

        var totalItems = lines.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)PageSize));
        var currentPage = Math.Clamp(requestedPage, 0, totalPages - 1);

        var pageLines = lines
            .Skip(currentPage * PageSize)
            .Take(PageSize)
            .ToList();

        var embed = new EmbedBuilder()
            .WithTitle("📊 История ивентов")
            .WithDescription($"**Ведущий:** <@{targetUserId}>\n\n" +
                             (pageLines.Count > 0 ? string.Join("\n\n", pageLines) : "_У пользователя пока нет проведённых ивентов._"))
            .WithColor(new Color(43, 45, 49))
            .Build();

        var compBuilder = new ComponentBuilder();
        if (totalPages > 1)
        {
            compBuilder
                .WithButton("◀", $"ev:0:hist_page:{targetUserId}:{Math.Max(0, currentPage - 1)}", ButtonStyle.Secondary, disabled: currentPage <= 0)
                .WithButton("▶", $"ev:0:hist_page:{targetUserId}:{Math.Min(totalPages - 1, currentPage + 1)}", ButtonStyle.Secondary, disabled: currentPage >= totalPages - 1);
        }

        return (embed, compBuilder.Build());
    }
}
