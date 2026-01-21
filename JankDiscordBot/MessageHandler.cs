using Discord.WebSocket;
using GloomhavenRotationBot.Services;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot;

/// <summary>
/// Handles incoming Discord messages and routes them to the chatbot service.
/// </summary>
public sealed class MessageHandler
{
    private readonly DiscordSocketClient _client;
    private readonly ChatbotService _chatbot;
    private readonly AppSettingsService _settings;
    private readonly ILogger<MessageHandler> _logger;

    public MessageHandler(
        DiscordSocketClient client,
        ChatbotService chatbot,
        AppSettingsService settings,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _chatbot = chatbot;
        _settings = settings;
        _logger = logger;
    }

    public Task InitializeAsync()
    {
        _client.MessageReceived += OnMessageReceivedAsync;
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(SocketMessage msg)
    {
        try
        {
            // Ignore bot messages
            if (msg.Author.IsBot) return;

            // Only respond in the configured guild
            var (_, guildId, _) = await _settings.GetDiscordConfigAsync();
            if (guildId == 0) return;

            if (msg.Channel is not SocketGuildChannel guildChannel || guildChannel.Guild.Id != guildId)
                return;

            var content = msg.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content)) return;

            // Only respond when the bot is explicitly mentioned
            var botMentioned = msg.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);
            if (!botMentioned) return;

            // Don't respond if chatbot is paused
            if (_chatbot.IsPaused()) return;

            var aiResponse = await _chatbot.GenerateResponseAsync(content, msg.Author.Id, msg.Author.Username);
            if (!string.IsNullOrWhiteSpace(aiResponse))
            {
                await msg.Channel.SendMessageAsync(aiResponse);
                _logger.LogInformation("Responded via AI to {User}: {Message}", msg.Author.Username, content);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message from {User}", msg.Author?.Username ?? "Unknown");
        }
    }
}
