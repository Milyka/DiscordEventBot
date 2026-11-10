# DiscordEventBot — Event Workspace Orchestrator & Moderation Bot (.NET 10 / EF Core)

I wrote **DiscordEventBot** (~9.2k LOC in **C# 13 / .NET 10** and **Entity Framework Core**) to automate how community events are hosted, moderated, and cleaned up on large Discord servers.

When an organizer starts an event, the bot automatically builds a dedicated 4-channel workspace, routes all participant button/modal interactions, enforces a 7-tier staff hierarchy, and batches live embed updates so the bot never gets rate-limited by Discord when dozens of players sign up at the same time.

```text
DiscordEventBot.sln
└── DiscordEventBot/
    ├── Commands/        # /event slash commands, dynamic autocomplete handlers & modals
    ├── Configuration/   # Strongly-typed IOptions config (BotSettings.cs — roles, limits, presets)
    ├── Data/            # EF Core SQLite context (BotDbContext.cs) & 8 relational entities
    ├── Services/        # Core event engine, rate-limit embed queue, interaction router & permissions
    └── Workers/         # Background IHostedService daemons for auto-closing & punishment expiry
```

---

## Key Technical Pieces

### 1. Preventing Discord 429 Rate Limits (`EmbedUpdateQueueService.cs`)
During peak events, 20–40 users often click **"Join"** within a couple of seconds. Editing the registration embed on every single click hits Discord's `5 edits / 5s` per-channel limit almost immediately.
* To fix this, I built `EmbedUpdateQueueService`—an asynchronous coalescing queue that deduplicates and throttles pending UI refreshes per `EventId`, keeping button interactions instant while batching the actual Discord REST API calls.

### 2. Automated 4-Channel Event Workspaces (`EventService.cs` — 110 KB)
Running `/event start` provisions a full event environment under the target category with custom permission overwrites (`PermissionService.cs`):
* **Host & Mod Panel (`ManageChannelId`)**: Private control room with two interactive dashboards (`ManageMainMessageId` for room/voice states and `ManageModMessageId` for paginated participant actions).
* **Public Info & Sign-Up (`InfoChannelId`)**: Live embed showing the game preset, host, join instructions (`InstructionLink`, `InstructionCode`), and current participant list.
* **Participant Text Chat (`ChatChannelId`)**: Locked to registered event participants.
* **Primary & Sub-Room Voice Channels (`VoiceChannelId` + `EventRoomEntity`)**: Hosts can spawn extra voice sub-rooms (`/event room`, `/event panel`) for team splits and toggle global voice states (`IsVoiceClosed`, `IsAllMuted`, `IsAllDeafened`).

### 3. 7-Tier Staff Hierarchy & 3-Strike Auto-Moderation (`HierarchyService.cs`, `PunishmentService.cs`)
* **Hierarchy Checks (`StaffRank`)**: Compares staff ranks (`EventMod` → `Master` → `Curator` → `Mentor` → `Admingroup` → `StaffAdmin` → `Developer`) so lower-ranked moderators cannot override or close an event hosted by a higher-ranked organizer (`/event control`).
* **Warning Escalation & Expiry Daemons**: Tracks per-user warnings (`WarningEntity`) and automatically kicks or applies a timed event ban (`PunishmentEntity`) when `WarningsBeforeKick` (default: 3) is reached. Background workers (`AutoCloseAndCleanupWorker.cs`, `PunishmentExpiryWorker.cs`) clean up idle events and lift expired bans automatically.

---

## Running Locally

1. **Clone the repo:**
   ```bash
   git clone https://github.com/Milyka/DiscordEventBot.git
   cd DiscordEventBot
   ```
2. **Configure `settings.json`:**
   Copy `settings.example.json` to `DiscordEventBot/settings.json` and set your Discord Bot Token:
   ```json
   {
     "Token": "YOUR_DISCORD_BOT_TOKEN_HERE",
     "GuildId": 0,
     "DatabasePath": "events.db"
   }
   ```
3. **Run the worker:**
   ```bash
   dotnet build DiscordEventBot.sln -c Release
   dotnet run --project DiscordEventBot/DiscordEventBot.csproj
   ```
