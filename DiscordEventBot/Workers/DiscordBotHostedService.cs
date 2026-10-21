using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using DiscordEventBot.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Workers;

public sealed class DiscordBotHostedService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _serviceProvider;
    private readonly EventInteractionRouter _interactionRouter;
    private readonly EventService _eventService;
    private readonly LoggingService _loggingService;
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;
    private readonly ILogger<DiscordBotHostedService> _logger;
    private bool _isInitialized;

    public DiscordBotHostedService(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider serviceProvider,
        EventInteractionRouter interactionRouter,
        EventService eventService,
        LoggingService loggingService,
        IOptionsMonitor<BotSettings> settingsMonitor,
        ILogger<DiscordBotHostedService> logger)
    {
        _client = client;
        _interactions = interactions;
        _serviceProvider = serviceProvider;
        _interactionRouter = interactionRouter;
        _eventService = eventService;
        _loggingService = loggingService;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += OnDiscordLogAsync;
        _interactions.Log += OnDiscordLogAsync;
        _client.Ready += OnClientReadyAsync;
        _client.InteractionCreated += OnInteractionCreatedAsync;
        _client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;

        await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _serviceProvider);

        var token = _settingsMonitor.CurrentValue.Token;
        if (string.IsNullOrWhiteSpace(token) || token == "BOT_TOKEN")
        {
            _logger.LogCritical("Не задан валидный токен бота в settings.json! Укажите Token в файле settings.json и перезапустите приложение.");
            return;
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Log -= OnDiscordLogAsync;
        _interactions.Log -= OnDiscordLogAsync;
        _client.Ready -= OnClientReadyAsync;
        _client.InteractionCreated -= OnInteractionCreatedAsync;
        _client.UserVoiceStateUpdated -= OnUserVoiceStateUpdatedAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;

        await _client.StopAsync();
    }

    private Task OnDiscordLogAsync(LogMessage msg)
    {
        var level = msg.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(level, msg.Exception, "[Discord:{Source}] {Message}", msg.Source, msg.Message);
        return Task.CompletedTask;
    }

    private async Task OnClientReadyAsync()
    {
        try
        {
            var configuredGuildId = _settingsMonitor.CurrentValue.GuildId;
            if (configuredGuildId != 0 && _client.GetGuild(configuredGuildId) is not null)
            {
                await _interactions.RegisterCommandsToGuildAsync(configuredGuildId, deleteMissing: true);
                _logger.LogInformation("Slash-команды зарегистрированы для сервера {GuildId}.", configuredGuildId);
            }
            else
            {
                foreach (var guild in _client.Guilds)
                {
                    await _interactions.RegisterCommandsToGuildAsync(guild.Id, deleteMissing: true);
                    _logger.LogInformation("Slash-команды зарегистрированы для сервера {GuildName} ({GuildId}).", guild.Name, guild.Id);
                }
            }

            if (!_isInitialized)
            {
                _isInitialized = true;
                await _eventService.SyncDatabaseWithServerOnStartupAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при инициализации команд и синхронизации БД в событии Ready.");
        }
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            if (_interactionRouter.CanHandle(interaction))
            {
                await _interactionRouter.HandleAsync(interaction);
                return;
            }

            var ctx = new SocketInteractionContext(_client, interaction);
            var result = await _interactions.ExecuteCommandAsync(ctx, _serviceProvider);

            if (!result.IsSuccess && !interaction.HasResponded)
            {
                _logger.LogWarning("Interaction error ({Error}): {Reason}", result.Error, result.ErrorReason);
                await interaction.RespondAsync($"Ошибка выполнения команды: {result.ErrorReason}", ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Необработанное исключение при обработке интеракшена {InteractionId}.", interaction.Id);

            if (!interaction.HasResponded)
            {
                try
                {
                    await interaction.RespondAsync("Произошла ошибка при обработке действия.", ephemeral: true);
                }
                catch
                {
                }
            }
        }
    }

    private async Task OnUserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState oldState, SocketVoiceState newState)
    {
        try
        {
            await _eventService.HandleUserVoiceStateUpdatedAsync(user, oldState, newState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при обработке UserVoiceStateUpdated для пользователя {UserId}.", user.Id);
        }
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        try
        {
            await _eventService.HandleManagementChannelMessageReceivedAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при обработке сообщения в канале управления.");
        }
    }
}
