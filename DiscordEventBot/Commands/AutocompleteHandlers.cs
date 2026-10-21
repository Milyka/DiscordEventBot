using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordEventBot.Data;
using DiscordEventBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordEventBot.Commands;

public sealed class ControlEventAutocompleteHandler : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        if (context.Guild is not SocketGuild guild || context.User is not IGuildUser actorUser)
            return AutocompletionResult.FromSuccess();

        var hierarchy = services.GetRequiredService<HierarchyService>();
        if (!hierarchy.HasEventAccess(actorUser))
            return AutocompletionResult.FromSuccess();

        var dbFactory = services.GetRequiredService<IDbContextFactory<BotDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var activeEvents = await db.Events
            .AsNoTracking()
            .Where(e => e.GuildId == guild.Id && e.Status == EventStatus.Active)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ToListAsync();

        var userInput = autocompleteInteraction.Data.Current.Value?.ToString()?.Trim() ?? string.Empty;
        var results = new List<AutocompleteResult>();

        foreach (var ev in activeEvents)
        {
            if (!hierarchy.IsDeveloper(actorUser) && !hierarchy.CanControl(guild, actorUser.Id, ev.HostUserId))
                continue;

            var hostUser = guild.GetUser(ev.HostUserId);
            var hostName = hostUser?.DisplayName ?? hostUser?.Username ?? ev.HostUserId.ToString(CultureInfo.InvariantCulture);
            var label = $"{ev.Name} — @{hostName}";
            if (label.Length > 100)
                label = label[..100];

            if (!string.IsNullOrEmpty(userInput) &&
                !label.Contains(userInput, StringComparison.OrdinalIgnoreCase) &&
                !ev.Id.ToString(CultureInfo.InvariantCulture).Contains(userInput, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(new AutocompleteResult(label, ev.Id.ToString(CultureInfo.InvariantCulture)));
            if (results.Count >= 25)
                break;
        }

        return AutocompletionResult.FromSuccess(results);
    }
}

public sealed class ManageableEventAutocompleteHandler : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        if (context.Guild is not SocketGuild guild || context.User is not IGuildUser actorUser)
            return AutocompletionResult.FromSuccess();

        var hierarchy = services.GetRequiredService<HierarchyService>();
        if (!hierarchy.HasEventAccess(actorUser))
            return AutocompletionResult.FromSuccess();

        var dbFactory = services.GetRequiredService<IDbContextFactory<BotDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var activeEvents = await db.Events
            .AsNoTracking()
            .Where(e => e.GuildId == guild.Id && e.Status == EventStatus.Active)
            .OrderByDescending(e => e.HostUserId == actorUser.Id)
            .ThenByDescending(e => e.CreatedAtUtc)
            .ToListAsync();

        var userInput = autocompleteInteraction.Data.Current.Value?.ToString()?.Trim() ?? string.Empty;
        var results = new List<AutocompleteResult>();

        foreach (var ev in activeEvents)
        {
            if (!hierarchy.CanManageOrControlEvent(guild, actorUser, ev))
                continue;

            var hostUser = guild.GetUser(ev.HostUserId);
            var hostName = hostUser?.DisplayName ?? hostUser?.Username ?? ev.HostUserId.ToString(CultureInfo.InvariantCulture);
            var prefix = ev.HostUserId == actorUser.Id ? "⭐ " : string.Empty;
            var label = $"{prefix}{ev.Name} — @{hostName}";
            if (label.Length > 100)
                label = label[..100];

            if (!string.IsNullOrEmpty(userInput) &&
                !label.Contains(userInput, StringComparison.OrdinalIgnoreCase) &&
                !ev.Id.ToString(CultureInfo.InvariantCulture).Contains(userInput, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(new AutocompleteResult(label, ev.Id.ToString(CultureInfo.InvariantCulture)));
            if (results.Count >= 25)
                break;
        }

        return AutocompletionResult.FromSuccess(results);
    }
}

public sealed class EventRoomAutocompleteHandler : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        if (context.Guild is not SocketGuild guild || context.User is not IGuildUser actorUser)
            return AutocompletionResult.FromSuccess();

        var hierarchy = services.GetRequiredService<HierarchyService>();
        if (!hierarchy.HasEventAccess(actorUser))
            return AutocompletionResult.FromSuccess();

        var dbFactory = services.GetRequiredService<IDbContextFactory<BotDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var activeEvents = await db.Events
            .Include(e => e.AdditionalRooms)
            .AsNoTracking()
            .Where(e => e.GuildId == guild.Id && e.Status == EventStatus.Active)
            .OrderByDescending(e => e.HostUserId == actorUser.Id)
            .ThenByDescending(e => e.CreatedAtUtc)
            .ToListAsync();

        var userInput = autocompleteInteraction.Data.Current.Value?.ToString()?.Trim() ?? string.Empty;
        var results = new List<AutocompleteResult>();

        foreach (var ev in activeEvents)
        {
            if (ev.HostUserId != actorUser.Id && !hierarchy.IsDeveloper(actorUser))
                continue;

            foreach (var room in ev.AdditionalRooms.OrderBy(r => r.CreatedAtUtc))
            {
                var label = $"{room.Name} ({room.MaxParticipants} чел.)";
                if (label.Length > 100)
                    label = label[..100];

                if (!string.IsNullOrEmpty(userInput) &&
                    !room.Name.Contains(userInput, StringComparison.OrdinalIgnoreCase) &&
                    !room.Id.ToString(CultureInfo.InvariantCulture).Contains(userInput, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new AutocompleteResult(label, room.Id.ToString(CultureInfo.InvariantCulture)));
                if (results.Count >= 25)
                    break;
            }
        }

        return AutocompletionResult.FromSuccess(results);
    }
}
