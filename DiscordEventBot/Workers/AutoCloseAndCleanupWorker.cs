using Discord.WebSocket;
using DiscordEventBot.Configuration;
using DiscordEventBot.Data;
using DiscordEventBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Workers;

public sealed class AutoCloseAndCleanupWorker : BackgroundService
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly DiscordSocketClient _client;
    private readonly EventService _eventService;
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;
    private readonly ILogger<AutoCloseAndCleanupWorker> _logger;

    public AutoCloseAndCleanupWorker(
        IDbContextFactory<BotDbContext> dbFactory,
        DiscordSocketClient client,
        EventService eventService,
        IOptionsMonitor<BotSettings> settingsMonitor,
        ILogger<AutoCloseAndCleanupWorker> logger)
    {
        _dbFactory = dbFactory;
        _client = client;
        _eventService = eventService;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckInactiveEventsAsync(stoppingToken);
                await CleanupOldRecordsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в фоновой службе автозакрытия и очистки БД.");
            }

            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }

    private async Task CheckInactiveEventsAsync(CancellationToken ct)
    {
        var limits = _settingsMonitor.CurrentValue.Event.Limits;
        var autoCloseHours = limits.AutoCloseHours;
        if (autoCloseHours <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddHours(-autoCloseHours);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidateEvents = await db.Events
            .Include(e => e.Participants)
            .AsNoTracking()
            .Where(e => e.Status == EventStatus.Active && e.LastActivityAtUtc <= cutoff)
            .ToListAsync(ct);

        foreach (var ev in candidateEvents)
        {
            var guild = _client.GetGuild(ev.GuildId);
            var voiceChannel = guild?.GetVoiceChannel(ev.VoiceChannelId);
            var hasUsersInVoice = voiceChannel is not null
                ? voiceChannel.ConnectedUsers.Any(u => !u.IsBot)
                : ev.Participants.Any(p => p.IsInVoice);

            if (hasUsersInVoice)
                continue;

            _logger.LogInformation("Автозакрытие неактивного ивента #{EventId} ({Name}) — {Hours} ч. без активности.",
                ev.Id, ev.Name, autoCloseHours);

            await _eventService.CloseEventAsync(
                ev.Id,
                actorUserId: null,
                closeReason: $"Неактивный ивент (автозакрытие через {autoCloseHours} ч. бездействия)");
        }
    }

    private async Task CleanupOldRecordsAsync(CancellationToken ct)
    {
        var retentionDays = _settingsMonitor.CurrentValue.Event.Limits.RetentionDays;
        if (retentionDays <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var oldClosedEvents = await db.Events
            .Where(e => e.Status == EventStatus.Closed && e.ClosedAtUtc != null && e.ClosedAtUtc < cutoff)
            .ToListAsync(ct);

        if (oldClosedEvents.Count > 0)
        {
            db.Events.RemoveRange(oldClosedEvents);
        }

        var oldHistory = await db.EventHistory
            .Where(h => h.EndedAtUtc < cutoff)
            .ToListAsync(ct);

        if (oldHistory.Count > 0)
        {
            db.EventHistory.RemoveRange(oldHistory);
        }

        var oldControlLogs = await db.ControlLogs
            .Where(c => c.CreatedAtUtc < cutoff)
            .ToListAsync(ct);

        if (oldControlLogs.Count > 0)
        {
            db.ControlLogs.RemoveRange(oldControlLogs);
        }

        var oldPunishments = await db.Punishments
            .Where(p => !p.IsActive && p.CreatedAtUtc < cutoff)
            .ToListAsync(ct);

        if (oldPunishments.Count > 0)
        {
            db.Punishments.RemoveRange(oldPunishments);
        }

        var oldWarnings = await db.Warnings
            .Where(w => w.CreatedAtUtc < cutoff)
            .ToListAsync(ct);

        if (oldWarnings.Count > 0)
        {
            db.Warnings.RemoveRange(oldWarnings);
        }

        if (oldClosedEvents.Count > 0 || oldHistory.Count > 0 || oldControlLogs.Count > 0 ||
            oldPunishments.Count > 0 || oldWarnings.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Очистка устаревших записей старше {Days} дн. завершена.", retentionDays);
        }
    }
}
