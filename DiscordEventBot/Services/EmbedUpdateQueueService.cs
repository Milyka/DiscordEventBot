using System.Collections.Concurrent;
using Discord.WebSocket;
using DiscordEventBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiscordEventBot.Services;

public sealed class EmbedUpdateQueueService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(2500);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DiscordSocketClient _client;
    private readonly EmbedAndComponentBuilderService _embedBuilder;
    private readonly ILogger<EmbedUpdateQueueService> _logger;

    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingUpdates = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _eventLocks = new();

    public EmbedUpdateQueueService(
        IServiceScopeFactory scopeFactory,
        DiscordSocketClient client,
        EmbedAndComponentBuilderService embedBuilder,
        ILogger<EmbedUpdateQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _embedBuilder = embedBuilder;
        _logger = logger;
    }

    public void EnqueueEventEmbedUpdate(int eventId)
    {
        var newCts = new CancellationTokenSource();
        if (_pendingUpdates.TryRemove(eventId, out var oldCts))
        {
            try
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            catch
            {
            }
        }

        _pendingUpdates[eventId] = newCts;
        _ = RunDebouncedUpdateAsync(eventId, newCts);
    }

    private async Task RunDebouncedUpdateAsync(int eventId, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DebounceDelay, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            await RefreshEventMessagesNowAsync(eventId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отложенном обновлении Embed ивента #{EventId}.", eventId);
        }
        finally
        {
            if (_pendingUpdates.TryGetValue(eventId, out var current) && ReferenceEquals(current, cts))
            {
                _pendingUpdates.TryRemove(eventId, out _);
            }
        }
    }

    public async Task RefreshEventMessagesNowAsync(int eventId)
    {
        var semaphore = _eventLocks.GetOrAdd(eventId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var ev = await db.Events
                .Include(e => e.Participants)
                .Include(e => e.Punishments)
                .Include(e => e.Warnings)
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == eventId);

            if (ev is null || ev.Status != EventStatus.Active)
                return;

            var guild = _client.GetGuild(ev.GuildId);
            if (guild is null)
                return;

            var participantsCount = ev.Participants.Count;
            var inVoiceCount = ev.Participants.Count(p => p.IsInVoice);
            var activePunishments = ev.Punishments.Where(p => p.IsActive).ToList();
            var activeBansCount = activePunishments.Count(p => p.Type == PunishmentType.Ban);
            var kicksCount = ev.Punishments.Count(p => p.Type == PunishmentType.Kick);
            var warningsCount = ev.Warnings.Count;

            if (ev.InfoChannelId != 0 && ev.InfoMessageId != 0 &&
                guild.GetTextChannel(ev.InfoChannelId) is SocketTextChannel infoChannel)
            {
                try
                {
                    var (infoEmbed, infoComponents) = _embedBuilder.BuildInfoChannelMessage(ev, participantsCount, inVoiceCount);
                    await infoChannel.ModifyMessageAsync(ev.InfoMessageId, msg =>
                    {
                        msg.Embed = infoEmbed;
                        msg.Components = infoComponents;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось обновить сообщение информации для ивента #{EventId}.", eventId);
                }
            }

            if (ev.ManageChannelId != 0 &&
                guild.GetTextChannel(ev.ManageChannelId) is SocketTextChannel manageChannel)
            {
                if (ev.ManageMainMessageId != 0)
                {
                    try
                    {
                        var mainEmbed = _embedBuilder.BuildManagementMainEmbed(
                            ev, participantsCount, inVoiceCount, activeBansCount, kicksCount, warningsCount);
                        var mainComponents = _embedBuilder.BuildManagementMainComponents(ev);

                        await manageChannel.ModifyMessageAsync(ev.ManageMainMessageId, msg =>
                        {
                            msg.Embed = mainEmbed;
                            msg.Components = mainComponents;
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось обновить сообщение 1 в #управление для ивента #{EventId}.", eventId);
                    }
                }

                if (ev.ManageModMessageId != 0)
                {
                    try
                    {
                        var (modEmbed, modComponents) = _embedBuilder.BuildModerationMessage(
                            guild, ev, ev.Participants, activePunishments, ev.Warnings);

                        await manageChannel.ModifyMessageAsync(ev.ManageModMessageId, msg =>
                        {
                            msg.Embed = modEmbed;
                            msg.Components = modComponents;
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось обновить сообщение 2 в #управление для ивента #{EventId}.", eventId);
                    }
                }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }
}
