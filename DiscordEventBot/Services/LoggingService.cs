using Discord;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Services;

public enum LogCategory
{
    Errors,
    OrganizerActions,
    EventCreateDelete,
    JoinLeave,
    Close,
    Pings,
    Punishments,
    Control
}

public sealed class LoggingService
{
    private readonly DiscordSocketClient _client;
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;
    private readonly ILogger<LoggingService> _logger;

    public LoggingService(
        DiscordSocketClient client,
        IOptionsMonitor<BotSettings> settingsMonitor,
        ILogger<LoggingService> logger)
    {
        _client = client;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    public async Task LogEventAsync(
        LogCategory category,
        string title,
        string what,
        ulong? actorUserId = null,
        ulong? targetUserId = null,
        string? whereInfo = null,
        Color? color = null)
    {
        try
        {
            var settings = _settingsMonitor.CurrentValue;
            var logConfig = settings.Event.Logging;

            _logger.LogInformation("[{Category}] {Title}: {What} (Actor={Actor}, Target={Target}, Where={Where})",
                category, title, what, actorUserId, targetUserId, whereInfo);

            if (!logConfig.Enabled)
                return;

            var isCategoryEnabled = category switch
            {
                LogCategory.Errors => logConfig.Errors,
                LogCategory.OrganizerActions => logConfig.OrganizerActions,
                LogCategory.EventCreateDelete => logConfig.EventCreateDelete,
                LogCategory.JoinLeave => logConfig.JoinLeave,
                LogCategory.Close => logConfig.Close,
                LogCategory.Pings => logConfig.Pings,
                LogCategory.Punishments => logConfig.Punishments,
                LogCategory.Control => logConfig.Control,
                _ => true
            };

            if (!isCategoryEnabled)
                return;

            var logChannelId = settings.Event.LogChannelId;
            if (logChannelId == 0)
                return;

            if (_client.GetChannel(logChannelId) is not IMessageChannel logChannel)
                return;

            var embedColor = color ?? category switch
            {
                LogCategory.Errors => Color.DarkRed,
                LogCategory.Punishments => Color.Red,
                LogCategory.Control => Color.Orange,
                LogCategory.EventCreateDelete => Color.Green,
                LogCategory.Close => new Color(43, 45, 49),
                LogCategory.JoinLeave => Color.Teal,
                LogCategory.Pings => Color.Gold,
                _ => new Color(43, 45, 49)
            };

            var safeDescription = string.IsNullOrWhiteSpace(what)
                ? string.Empty
                : what.Length > 4000
                    ? what[..4000] + "..."
                    : what;

            var builder = new EmbedBuilder()
                .WithTitle(title)
                .WithDescription(safeDescription)
                .WithColor(embedColor)
                .AddField("Кто", actorUserId.HasValue ? $"<@{actorUserId.Value}>" : "Система", inline: true)
                .AddField("Где", string.IsNullOrWhiteSpace(whereInfo) ? "Сервер" : whereInfo, inline: true);

            if (targetUserId.HasValue)
            {
                builder.AddField("Участник", $"<@{targetUserId.Value}>", inline: true);
            }

            await logChannel.SendMessageAsync(embed: builder.Build());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось отправить сообщение в лог-канал Discord.");
        }
    }
}
