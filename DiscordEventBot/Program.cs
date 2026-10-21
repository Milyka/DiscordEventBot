using System.Data.Common;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordEventBot.Configuration;
using DiscordEventBot.Data;
using DiscordEventBot.Services;
using DiscordEventBot.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordEventBot;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var settingsPath = EnsureSettingsFileExists();

        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration.Sources.Clear();
        builder.Configuration
            .SetBasePath(Path.GetDirectoryName(settingsPath) ?? Directory.GetCurrentDirectory())
            .AddJsonFile(settingsPath, optional: false, reloadOnChange: true)
            .AddEnvironmentVariables();

        builder.Services.Configure<BotSettings>(builder.Configuration);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
            options.SingleLine = true;
        });
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.Error);

        var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "events.db";
        var fullDbPath = Path.IsPathRooted(dbPath)
            ? dbPath
            : Path.Combine(Directory.GetCurrentDirectory(), dbPath);

        builder.Services.AddDbContextFactory<BotDbContext>(options =>
        {
            options.UseSqlite(
                $"Data Source={fullDbPath}",
                sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        });

        builder.Services.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            AlwaysDownloadUsers = true,
            LogLevel = LogSeverity.Info,
            UseInteractionSnowflakeDate = false
        }));

        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<DiscordSocketClient>();
            return new InteractionService(client.Rest, new InteractionServiceConfig
            {
                DefaultRunMode = RunMode.Sync,
                LogLevel = LogSeverity.Info,
                UseCompiledLambda = true
            });
        });

        builder.Services.AddSingleton<HierarchyService>();
        builder.Services.AddSingleton<AntiAbuseService>();
        builder.Services.AddSingleton<LoggingService>();
        builder.Services.AddSingleton<PermissionService>();
        builder.Services.AddSingleton<EmbedAndComponentBuilderService>();
        builder.Services.AddSingleton<EmbedUpdateQueueService>();
        builder.Services.AddSingleton<PunishmentService>();
        builder.Services.AddSingleton<EventService>();
        builder.Services.AddSingleton<GamePresetsSettingsService>();
        builder.Services.AddSingleton<LogAndHistoryBuilderService>();
        builder.Services.AddSingleton<EventInteractionRouter>();

        builder.Services.AddHostedService<DiscordBotHostedService>();
        builder.Services.AddHostedService<PunishmentExpiryWorker>();
        builder.Services.AddHostedService<AutoCloseAndCleanupWorker>();

        using var host = builder.Build();

        await InitializeDatabaseAsync(host.Services);

        await host.RunAsync();
    }

    private static string EnsureSettingsFileExists()
    {
        var currentDirSettings = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");
        var currentDirExample = Path.Combine(Directory.GetCurrentDirectory(), "settings.example.json");
        var subDirSettings = Path.Combine(Directory.GetCurrentDirectory(), "DiscordEventBot", "settings.json");
        var baseDirSettings = Path.Combine(AppContext.BaseDirectory, "settings.json");
        var baseDirExample = Path.Combine(AppContext.BaseDirectory, "settings.example.json");

        if (!File.Exists(currentDirSettings))
        {
            if (File.Exists(subDirSettings))
                File.Copy(subDirSettings, currentDirSettings);
            else if (File.Exists(currentDirExample))
                File.Copy(currentDirExample, currentDirSettings);
            else if (File.Exists(baseDirSettings))
                File.Copy(baseDirSettings, currentDirSettings);
            else if (File.Exists(baseDirExample))
                File.Copy(baseDirExample, currentDirSettings);
        }

        EnsureProductionDefaultsInSettingsFile(currentDirSettings);
        return Path.GetFullPath(currentDirSettings);
    }

    private static void EnsureProductionDefaultsInSettingsFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            var raw = File.ReadAllText(filePath);
            var root = JsonNode.Parse(raw) as JsonObject;
            if (root is null)
                return;

            bool changed = false;

            if ((root["GuildId"]?.GetValue<ulong>() ?? 0UL) == 0UL)
            {
                root["GuildId"] = 1060286255629344908UL;
                changed = true;
            }

            if (root["Event"] is JsonObject evObj)
            {
                if ((evObj["AnchorCategoryId"]?.GetValue<ulong>() ?? 0UL) == 0UL)
                {
                    evObj["AnchorCategoryId"] = 1463206062424461547UL;
                    changed = true;
                }

                if ((evObj["LogChannelId"]?.GetValue<ulong>() ?? 0UL) == 0UL)
                {
                    evObj["LogChannelId"] = 1568748571770228917UL;
                    changed = true;
                }

                if (evObj["Roles"] is JsonObject rolesObj)
                {
                    var roleDefaults = new Dictionary<string, ulong>
                    {
                        ["Male"] = 1463204419075047606UL,
                        ["Female"] = 1463204426268540929UL,
                        ["EventMod"] = 1538176463835103323UL,
                        ["Master"] = 1538176463835103323UL,
                        ["Curator"] = 1538176463835103323UL,
                        ["Admingroup"] = 1538176463835103323UL,
                        ["Mentor"] = 1538176463835103323UL,
                        ["StaffAdmin"] = 1538176463835103323UL,
                        ["Developer"] = 1463204361583722527UL,
                        ["EventBan"] = 1463204461441843283UL,
                        ["Ping"] = 1568749136960819210UL
                    };

                    foreach (var kvp in roleDefaults)
                    {
                        if ((rolesObj[kvp.Key]?.GetValue<ulong>() ?? 0UL) == 0UL)
                        {
                            rolesObj[kvp.Key] = kvp.Value;
                            changed = true;
                        }
                    }
                }
            }

            if (changed)
            {
                var writeOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                File.WriteAllText(filePath, root.ToJsonString(writeOptions));
            }
        }
        catch
        {
        }
    }

    private static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<BotDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        await ExecuteNonQuerySafeAsync(conn,
            "CREATE TABLE IF NOT EXISTS EventRooms (Id INTEGER PRIMARY KEY AUTOINCREMENT, EventId INTEGER NOT NULL, VoiceChannelId INTEGER NOT NULL, Name TEXT NOT NULL, MaxParticipants INTEGER NOT NULL, IsVoiceClosed INTEGER NOT NULL DEFAULT 0, IsAllMuted INTEGER NOT NULL DEFAULT 0, CreatedAtUtc TEXT NOT NULL, FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE CASCADE);");

        await EnsureColumnExistsAsync(conn, "Events", "InstructionLink", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnExistsAsync(conn, "Events", "InstructionCode", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnExistsAsync(conn, "Events", "InstructionText", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnExistsAsync(conn, "Events", "IsAllDeafened", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnExistsAsync(conn, "Participants", "LeftVoiceOrRegisteredAtUtc", "TEXT NULL");
        await EnsureColumnExistsAsync(conn, "EventRooms", "IsVoiceClosed", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnExistsAsync(conn, "EventRooms", "IsAllMuted", "INTEGER NOT NULL DEFAULT 0");
    }

    private static async Task EnsureColumnExistsAsync(DbConnection conn, string tableName, string columnName, string columnDef)
    {
        try
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({tableName});";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount > 1 && !reader.IsDBNull(1))
                    {
                        existingColumns.Add(reader.GetString(1));
                    }
                }
            }

            if (!existingColumns.Contains(columnName))
            {
                await ExecuteNonQuerySafeAsync(conn, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef};");
            }
        }
        catch
        {
        }
    }

    private static async Task ExecuteNonQuerySafeAsync(DbConnection conn, string sql)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
        }
    }
}
