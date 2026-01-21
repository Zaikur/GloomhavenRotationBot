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

            // Check if user is greeting the bot
            if (_chatbot.IsGreetingTheBot(content))
            {
                var response = _chatbot.GetGreetingResponse();
                await msg.Channel.SendMessageAsync(response);
                _logger.LogInformation("Responded to greeting from {User}", msg.Author.Username);
                return;
            }

            // Check if user is thanking the bot
            if (_chatbot.IsThankingTheBot(content))
            {
                var response = _chatbot.GetThankYouResponse();
                await msg.Channel.SendMessageAsync(response);
                _logger.LogInformation("Responded to thanks from {User}", msg.Author.Username);
                return;
            }

            // Check if user is asking about the next session
            if (_chatbot.IsAskingAboutNextSession(content))
            {
                var response = await _chatbot.GetNextSessionResponseAsync();
                if (response != null)
                {
                    await msg.Channel.SendMessageAsync(response);
                    _logger.LogInformation("Responded to question from {User}: {Question}", msg.Author.Username, content);
                }
                return;
            }

            // Check if user is asking if they are the DM
            if (_chatbot.IsAskingIfTheyAreDM(content))
            {
                var response = await _chatbot.CheckIfUserIsDMAsync(msg.Author.Id);
                if (response != null)
                {
                    await msg.Channel.SendMessageAsync(response);
                    _logger.LogInformation("Responded to self-check DM question from {User}", msg.Author.Username);
                }
                return;
            }

            // Check if user is asking about the DM
            if (_chatbot.IsAskingAboutDM(content))
            {
                var response = await _chatbot.GetDMResponseAsync();
                if (response != null)
                {
                    await msg.Channel.SendMessageAsync(response);
                    _logger.LogInformation("Responded to DM question from {User}", msg.Author.Username);
                }
                return;
            }

            // Check if user is asking if they are making food
            if (_chatbot.IsAskingIfTheyAreMakingFood(content))
            {
                var response = await _chatbot.CheckIfUserIsMakingFoodAsync(msg.Author.Id);
                if (response != null)
                {
                    await msg.Channel.SendMessageAsync(response);
                    _logger.LogInformation("Responded to self-check food question from {User}", msg.Author.Username);
                }
                return;
            }

            // Check if user is asking about who's making food
            if (_chatbot.IsAskingAboutFood(content))
            {
                var response = await _chatbot.GetFoodResponseAsync();
                if (response != null)
                {
                    await msg.Channel.SendMessageAsync(response);
                    _logger.LogInformation("Responded to food question from {User}", msg.Author.Username);
                }
                return;
            }

            // Check if user is asking about cancellation status
            if (_chatbot.IsAskingAboutCancellation(content))
            {
                var response = await _chatbot.GetCancellationStatusResponseAsync();
                if (response != null)
                {
                    await msg.Channel.SendMessageAsync(response);
                    _logger.LogInformation("Responded to cancellation question from {User}", msg.Author.Username);
                }
                return;
            }

            // Fallback when the bot is mentioned but the message is not understood
            var fallback = _chatbot.GetFallbackResponse();
            await msg.Channel.SendMessageAsync(fallback);
            _logger.LogInformation("Sent fallback response to {User}: {Message}", msg.Author.Username, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message from {User}", msg.Author?.Username ?? "Unknown");
        }
    }
}
