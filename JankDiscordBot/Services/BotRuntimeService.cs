using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

public sealed class BotRuntimeService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionHandler _handler;
    private readonly AppSettingsService _settings;
    private readonly BotStatusService _status;
    private readonly ILogger<BotRuntimeService> _logger;

    private string? _runningToken;
    private ulong _runningGuildId;
    private bool _runningRegisterToGuild;

    public BotRuntimeService(
        DiscordSocketClient client,
        InteractionHandler handler,
        AppSettingsService settings,
        BotStatusService status,
        ILogger<BotRuntimeService> logger)
    {
        _client = client;
        _handler = handler;
        _settings = settings;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.Log += msg =>
        {
            _logger.LogInformation("[Discord] {Message}", msg.ToString());
            return Task.CompletedTask;
        };

        await _handler.InitializeAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            var (token, guildId, registerToGuild) = await _settings.GetDiscordConfigAsync();

            if (string.IsNullOrWhiteSpace(token) || guildId == 0)
            {
                _status.Set("WaitingForSetup", "Set Discord token + guild id in the web UI.");
                await SafeStopAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            // If config changed, restart client
            var configChanged =
                _runningToken != token ||
                _runningGuildId != guildId ||
                _runningRegisterToGuild != registerToGuild;

            if (configChanged)
            {
                _status.Set("Restarting", "Discord config changed; restarting bot.");
                await SafeStopAsync();

                try
                {
                    _status.Set("Connecting", $"Connecting to guild {guildId}...");
                    await _client.LoginAsync(TokenType.Bot, token);
                    await _client.StartAsync();

                    _runningToken = token;
                    _runningGuildId = guildId;
                    _runningRegisterToGuild = registerToGuild;

                    _status.Set("Connected", $"Logged in. Commands will register on Ready. GuildId={guildId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start Discord client.");
                    _status.Set("Error", ex.Message);
                    await SafeStopAsync();
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        await SafeStopAsync();
        _status.Set("Stopped");
    }

    private async Task SafeStopAsync()
    {
        try { await _client.StopAsync(); } catch { }
        try { await _client.LogoutAsync(); } catch { }

        _runningToken = null;
        _runningGuildId = 0;
        _runningRegisterToGuild = true;
    }
}
