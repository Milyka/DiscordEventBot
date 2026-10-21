using DiscordEventBot.Data;
using DiscordEventBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordEventBot.Workers;

public sealed class PunishmentExpiryWorker : BackgroundService
{
    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly PunishmentService _punishmentService;
    private readonly EventService _eventService;
    private readonly ILogger<PunishmentExpiryWorker> _logger;

    public PunishmentExpiryWorker(
        IDbContextFactory<BotDbContext> dbFactory,
        PunishmentService punishmentService,
        EventService eventService,
        ILogger<PunishmentExpiryWorker> logger)
    {
        _dbFactory = dbFactory;
        _punishmentService = punishmentService;
        _eventService = eventService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExpiredPunishmentsAsync(stoppingToken);
                await _eventService.RemoveAbsentParticipantsAfterThreeMinutesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в фоновой службе таймеров.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task ProcessExpiredPunishmentsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var expiredMutes = await db.Punishments
            .AsNoTracking()
            .Where(p => p.IsActive &&
                        p.Type == PunishmentType.TimedMute &&
                        p.ExpiresAtUtc != null &&
                        p.ExpiresAtUtc <= now)
            .ToListAsync(ct);

        foreach (var mute in expiredMutes)
        {
            await _punishmentService.RemoveTimedMuteAsync(
                mute.EventId,
                actorUserId: null,
                targetUserId: mute.TargetUserId,
                isAutoExpired: true);
        }

        var expiredBans = await db.Punishments
            .AsNoTracking()
            .Where(p => p.IsActive &&
                        p.Type == PunishmentType.Ban &&
                        p.ExpiresAtUtc != null &&
                        p.ExpiresAtUtc <= now)
            .ToListAsync(ct);

        foreach (var ban in expiredBans)
        {
            await _punishmentService.RemoveExpiredBanAsync(ban.Id);
        }
    }
}
