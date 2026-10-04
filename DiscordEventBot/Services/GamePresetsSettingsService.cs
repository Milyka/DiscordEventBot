using System.Text.Encodings.Web;
using System.Text.Json;
using DiscordEventBot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Services;

public sealed class GamePresetsSettingsService
{
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;
    private readonly AntiAbuseService _antiAbuseService;
    private readonly ILogger<GamePresetsSettingsService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public GamePresetsSettingsService(
        IOptionsMonitor<BotSettings> settingsMonitor,
        AntiAbuseService antiAbuseService,
        ILogger<GamePresetsSettingsService> logger)
    {
        _settingsMonitor = settingsMonitor;
        _antiAbuseService = antiAbuseService;
        _logger = logger;
    }

    public IReadOnlyList<EventPreset> GetPresetsByCategory(string categoryKey)
    {
        return _settingsMonitor.CurrentValue.Event.Presets
            .Where(p => string.Equals(p.Category, categoryKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public EventPreset? GetPresetById(string presetId)
    {
        return _settingsMonitor.CurrentValue.Event.Presets
            .FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool Success, string Message, EventPreset? Preset)> AddGamePresetAsync(
        string categoryKey,
        string name,
        string? description,
        int maxParticipants)
    {
        var cleanName = name?.Trim() ?? string.Empty;
        var cleanDesc = description?.Trim() ?? string.Empty;
        var limits = _settingsMonitor.CurrentValue.Event.Limits;

        if (cleanName.Length is < 1 or > 100)
            return (false, "Название игры должно содержать от 1 до 100 символов.", null);

        if (cleanDesc.Length > 500)
            return (false, "Описание игры не может превышать 500 символов.", null);

        if (maxParticipants < limits.MinParticipants || maxParticipants > limits.MaxParticipants)
            return (false, $"Количество участников должно быть от {limits.MinParticipants} до {limits.MaxParticipants}.", null);

        if (!_antiAbuseService.ValidateText(cleanName, cleanDesc, out var abuseErr))
            return (false, abuseErr, null);

        await _fileLock.WaitAsync();
        try
        {
            var settings = _settingsMonitor.CurrentValue;
            if (settings.Event.Presets.Any(p =>
                string.Equals(p.Category, categoryKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Name, cleanName, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"Игра с названием «{cleanName}» уже существует в этой категории.", null);
            }

            var baseId = GenerateSlugId(cleanName);
            var uniqueId = baseId;
            var counter = 2;
            while (settings.Event.Presets.Any(p => string.Equals(p.Id, uniqueId, StringComparison.OrdinalIgnoreCase)))
            {
                uniqueId = $"{baseId}_{counter}";
                counter++;
            }

            var defaultEmoji = categoryKey.ToLowerInvariant() switch
            {
                "board" => "🎲",
                "roleplay" => "🎭",
                "entertainment" => "🎉",
                "multiplayer" => "🎮",
                _ => "🎮"
            };

            var newPreset = new EventPreset
            {
                Id = uniqueId,
                Category = categoryKey.ToLowerInvariant(),
                Name = cleanName,
                Emoji = defaultEmoji,
                IconUrl = string.Empty,
                Color = "#2B2D31",
                MaxParticipants = maxParticipants,
                Description = cleanDesc,
                PingRoleId = settings.Event.Roles.Ping != 0 ? settings.Event.Roles.Ping : 1568749136960819210UL
            };

            settings.Event.Presets.Add(newPreset);
            await SaveSettingsToDiskAsync(settings);

            return (true, $"Игра **{newPreset.Emoji} {newPreset.Name}** успешно добавлена в категорию **{EmbedAndComponentBuilderService.FormatCategoryName(categoryKey)}**!", newPreset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при сохранении новой игры в settings.json.");
            return (false, "Произошла ошибка при сохранении настроек в settings.json.", null);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<(bool Success, string Message, EventPreset? Preset)> EditGamePresetAsync(
        string presetId,
        string newName,
        string? newDescription,
        int newMaxParticipants)
    {
        var cleanName = newName?.Trim() ?? string.Empty;
        var cleanDesc = newDescription?.Trim() ?? string.Empty;
        var limits = _settingsMonitor.CurrentValue.Event.Limits;

        if (cleanName.Length is < 1 or > 100)
            return (false, "Название игры должно содержать от 1 до 100 символов.", null);

        if (cleanDesc.Length > 500)
            return (false, "Описание игры не может превышать 500 символов.", null);

        if (newMaxParticipants < limits.MinParticipants || newMaxParticipants > limits.MaxParticipants)
            return (false, $"Количество участников должно быть от {limits.MinParticipants} до {limits.MaxParticipants}.", null);

        if (!_antiAbuseService.ValidateText(cleanName, cleanDesc, out var abuseErr))
            return (false, abuseErr, null);

        await _fileLock.WaitAsync();
        try
        {
            var settings = _settingsMonitor.CurrentValue;
            var preset = settings.Event.Presets.FirstOrDefault(p =>
                string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));

            if (preset is null)
                return (false, "Игра не найдена в списке пресетов.", null);

            preset.Name = cleanName;
            preset.Description = cleanDesc;
            preset.MaxParticipants = newMaxParticipants;

            await SaveSettingsToDiskAsync(settings);

            return (true, $"Игра **{preset.Emoji} {preset.Name}** успешно обновлена и сохранена!", preset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при редактировании игры {PresetId} в settings.json.", presetId);
            return (false, "Произошла ошибка при сохранении изменений в settings.json.", null);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static async Task SaveSettingsToDiskAsync(BotSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonWriteOptions);
        var candidatePaths = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "settings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "DiscordEventBot", "settings.json"),
            Path.Combine(AppContext.BaseDirectory, "settings.json")
        };

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidatePaths)
        {
            var full = Path.GetFullPath(path);
            if (!written.Add(full))
                continue;

            if (path == candidatePaths[0] || File.Exists(full))
            {
                await File.WriteAllTextAsync(full, json);
            }
        }
    }

    private static string GenerateSlugId(string name)
    {
        var chars = name.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c))
            .Take(20)
            .ToArray();

        return chars.Length > 0 ? new string(chars) : $"game_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }
}
