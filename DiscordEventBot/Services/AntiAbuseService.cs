using System.Text.RegularExpressions;
using DiscordEventBot.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordEventBot.Services;

public sealed partial class AntiAbuseService
{
    private readonly IOptionsMonitor<BotSettings> _settingsMonitor;

    [GeneratedRegex(@"(?:https?://)?(?:www\.)?(?:discord\.gg|discord(?:app)?\.com/invite)/[a-zA-Z0-9\-_]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InviteLinkRegex();

    public AntiAbuseService(IOptionsMonitor<BotSettings> settingsMonitor)
    {
        _settingsMonitor = settingsMonitor;
    }

    public bool ValidateText(string? name, string? description, out string errorMessage)
    {
        errorMessage = string.Empty;
        var antiAbuse = _settingsMonitor.CurrentValue.Event.AntiAbuse;

        var combined = $"{name ?? string.Empty} {description ?? string.Empty}";

        if (antiAbuse.BlockInviteLinks)
        {
            if (InviteLinkRegex().IsMatch(combined)
                || combined.Contains("discord.gg", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("discord.com/invite", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("discordapp.com/invite", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Название содержит запрещенные слова или ссылки";
                return false;
            }
        }

        if (antiAbuse.BannedWords is { Count: > 0 })
        {
            foreach (var rawWord in antiAbuse.BannedWords)
            {
                var word = rawWord?.Trim();
                if (string.IsNullOrEmpty(word))
                    continue;

                if (combined.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = "Название содержит запрещенные слова или ссылки";
                    return false;
                }
            }
        }

        return true;
    }
}
