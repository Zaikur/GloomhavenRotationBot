# Copilot / Agent Instructions — GloomhavenRotationBot

Short, actionable guidance to help AI coding agents be productive in this repo.

1) Project summary
- Purpose: a self-hosted Discord bot + local Razor Pages UI to manage Gloomhaven rotations, announcements, and a session calendar (see README.md).
- Tech: .NET 8, Discord.Net (Socket + Interactions), ASP.NET Core Razor Pages, SQLite, Docker-friendly.

2) High-level architecture (what touches what)
- `Program.cs`: configures DI, Discord clients, hosted services, and LAN-only middleware. All services are registered here.
- `InteractionHandler.cs`: wires Discord.Interactions. Modules are auto-registered with `Assembly.GetExecutingAssembly()` and commands are registered either to a guild or globally in `OnReadyAsync`.
- `Services/*`: background jobs and helpers (e.g., `MorningAnnouncementService`, `AutoAdvanceService`, `BotRuntimeService`, `AnnouncementSender`, `ScheduleService`).
- `Data/*`: persistence layer and command modules (`Data/BotRepository.cs`, `Data/Modules/*` hold command modules like `RotationCommands` and `ScheduleCommands`).
- `Pages/*`: Razor UI (Setup, Calendar, Rosters etc.) — the UI writes settings through `AppSettingsService` and the DB.

3) Configuration & secrets
- The app stores the Discord token and config inside the local SQLite DB via `AppSettingsService` (see `GetDiscordConfigAsync` / `SaveDiscordConfigAsync`).
- For local development, use the Setup UI (`http://localhost:5055`) or populate the DB directly. The README documents Docker env vars used for deployment.

4) Command registration / adding interactions
- Location: `InteractionHandler.InitializeAsync` adds modules using `Assembly.GetExecutingAssembly()` — add new Interaction modules anywhere in the project (derive from `InteractionModuleBase`) and they will be discovered automatically.
- Registration: `OnReadyAsync` calls `_interactions.RegisterCommandsToGuildAsync(guildId, deleteMissing: true)` when `RegisterToGuild` is true (useful for development); otherwise it registers commands globally. To force guild registration during dev, set the guild id and the register flag in the Setup UI.

5) Common patterns & conventions agents should follow
- DI-first: add new services in `Program.cs` matching existing registrations (singletons for helpers, `AddHostedService` for background workers).
- Logging: use `ILogger<T>` and follow existing `LogInformation` / `LogError` patterns.
- Ephemeral interactions: commands are expected to respond privately where appropriate (see handler catch block that attempts `interaction.RespondAsync(..., ephemeral: true)`).
- LAN-only safety: the app restricts HTTP access to localhost/private IP ranges in `Program.cs`. Do not remove this unless explicitly exposing the UI.

6) Running & debugging
- Build: `dotnet build` at the solution root.
- Run locally: `dotnet run --project JankDiscordBot` (or open the solution in Visual Studio and run). Web UI default port is `5055` (see README).
- Docker: the repo includes a `Dockerfile` and README contains an example `docker-compose` snippet and GHCR image `ghcr.io/zaikur/gloomhavenrotationbot:latest`.

7) Database & persistence
- Uses SQLite stored on the mounted volume; all settings (including encrypted token) are stored via `BotRepository` and accessed by `AppSettingsService`.
- To reset or migrate: stop container, backup/remove DB file on volume, restart (see README section "To migrate or reset").

8) Integration points & external dependencies
- Discord API via `DiscordSocketClient` + `InteractionService` (configured in `Program.cs`). Ensure `GatewayIntents.Guilds | GatewayIntents.GuildMembers` are kept if you need member lists.
- Data protection: `AppSettingsService` uses `IDataProtectionProvider` to create a protector for the token — be careful changing DP config as it affects token encryption/decryption.

9) When modifying commands or services — checklist for agents
- If you add/remove interaction modules, update nothing else: they are auto-registered, but ensure names/permissions are correct and test registration locally with `RegisterToGuild=true`.
- If you add a new background worker, register it in `Program.cs` with `AddHostedService<YourService>()` and follow existing patterns for cancellation, logging, and DI.
- If you change schedule/announcement behavior, update `AppSettingsService` and any UI pages in `Pages/*` that surface those settings.

10) Files to inspect for examples
- Interaction wiring: `JankDiscordBot/InteractionHandler.cs`.
- DI & hosting: `JankDiscordBot/Program.cs`.
- Settings & token handling: `JankDiscordBot/Services/AppSettingsService.cs`.
- Persistence + command modules: `JankDiscordBot/Data/BotRepository.cs` and `JankDiscordBot/Data/Modules/`.
- Web UI: `JankDiscordBot/Pages/Setup.cshtml(.cs)` and other pages in `Pages/`.

If anything here is unclear or you want more detail about a particular subsystem (e.g., DB schema, a hosted service implementation, or how to test Interaction modules locally), tell me which area and I will expand or add concrete examples/tests.
