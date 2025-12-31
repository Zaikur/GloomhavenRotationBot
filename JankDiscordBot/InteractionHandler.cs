using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GloomhavenRotationBot.Services;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace GloomhavenRotationBot;

public sealed class InteractionHandler
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly AppSettingsService _settings;
    private readonly ILogger<InteractionHandler> _logger;

    public InteractionHandler(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider services,
        AppSettingsService settings,
        ILogger<InteractionHandler> logger)
    {
        _client = client;
        _interactions = interactions;
        _services = services;
        _settings = settings;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += OnInteractionAsync;

        _interactions.Log += msg =>
        {
            _logger.LogInformation("[Interactions] {Message}", msg.ToString());
            return Task.CompletedTask;
        };

        await _interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), _services);
    }

    private async Task OnReadyAsync()
    {
        try
        {
            var (_, guildId, registerToGuild) = await _settings.GetDiscordConfigAsync();

            // TODO: Once bot has applications.commands scope, enable guild registration
            await _interactions.RegisterCommandsGloballyAsync(deleteMissing: true);
            _logger.LogInformation("Registered commands globally.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register commands.");
        }
    }

    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var ctx = new SocketInteractionContext(_client, interaction);
            await _interactions.ExecuteCommandAsync(ctx, _services);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interaction failed.");
            try { await interaction.RespondAsync("Something went wrong handling that command.", ephemeral: true); }
            catch { }
        }
    }
}
