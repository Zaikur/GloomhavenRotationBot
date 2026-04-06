using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using GloomhavenRotationBot.Services;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot;

/// <summary>
/// Handles incoming Discord messages and routes them to the chatbot service.
/// </summary>
public sealed class MessageHandler
{
    private static readonly TimeSpan PurposePromptLifetime = TimeSpan.FromMinutes(10);

    private readonly DiscordSocketClient _client;
    private readonly BangResponseService _bangResponses;
    private readonly ChatbotService _chatbot;
    private readonly AppSettingsService _settings;
    private readonly ILogger<MessageHandler> _logger;
    private readonly ConcurrentDictionary<ulong, PendingPurposePrompt> _pendingPurposePrompts = new();

    private sealed record PendingPurposePrompt(ulong ChannelId, DateTime ExpiresUtc);

    public MessageHandler(
        DiscordSocketClient client,
        BangResponseService bangResponses,
        ChatbotService chatbot,
        AppSettingsService settings,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _bangResponses = bangResponses;
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

            if (await HandlePendingPurposeReplyAsync(msg, content))
                return;

            // Check for bang (!) commands first - these don't require bot mention
            if (content.StartsWith('!'))
            {
                await HandleBangCommandAsync(msg, content);
                return;
            }

            // Only respond when the bot is explicitly mentioned
            var botMentioned = msg.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);

            if (_bangResponses.LooksLikeBotInsult(content, _client.CurrentUser.Id, _client.CurrentUser.Username))
            {
                var insultResponse = _bangResponses.GetBotInsultResponse();
                await ReplyToMessageAsync(msg, insultResponse);
                _logger.LogInformation("Responded to directed insult from {User}", msg.Author.Username);
                return;
            }

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

    private async Task HandleBangCommandAsync(SocketMessage msg, string content)
    {
        try
        {
            var commandToken = content.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var command = commandToken.ToLowerInvariant();

            switch (command)
            {
                case "!nextsession":
                    var sessionResponse = await _chatbot.GetNextSessionResponseAsync();
                    if (sessionResponse != null)
                    {
                        await msg.Channel.SendMessageAsync(sessionResponse);
                        await MaybeAskPurposeQuestionAsync(msg);
                        _logger.LogInformation("Responded to !nextsession from {User}", msg.Author.Username);
                    }
                    break;

                case "!itsmybirthday":
                    var birthdayResponse = await _bangResponses.GetBirthdayResponseAsync(msg.Author.Id, msg.Author.Username);
                    await msg.Channel.SendMessageAsync(birthdayResponse);
                    await MaybeAskPurposeQuestionAsync(msg);
                    _logger.LogInformation("Responded to !itsmybirthday from {User}", msg.Author.Username);
                    break;

                case "!beans":
                    var beansResponse = _bangResponses.GetBeansResponse();
                    await ReplyToMessageAsync(msg, beansResponse);
                    await MaybeAskPurposeQuestionAsync(msg);
                    _logger.LogInformation("Responded to !beans from {User}", msg.Author.Username);
                    break;

                default:
                    var topicResponse = _bangResponses.GetGenericBangResponse(commandToken);
                    if (topicResponse == null)
                    {
                        _logger.LogInformation("Ignored bang command {Command} from {User}", commandToken, msg.Author.Username);
                        break;
                    }

                    await ReplyToMessageAsync(msg, topicResponse);
                    await MaybeAskPurposeQuestionAsync(msg);
                    _logger.LogInformation("Responded to bang command {Command} from {User}", commandToken, msg.Author.Username);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling bang command from {User}", msg.Author?.Username ?? "Unknown");
        }
    }

    private static async Task ReplyToMessageAsync(SocketMessage msg, string response)
    {
        await msg.Channel.SendMessageAsync(response, messageReference: new MessageReference(msg.Id));
    }

    private async Task MaybeAskPurposeQuestionAsync(SocketMessage msg)
    {
        if (await _settings.HasSeenPurposePromptAsync(msg.Author.Id))
            return;

        if (!_bangResponses.ShouldAskPurposeQuestion())
            return;

        if (_pendingPurposePrompts.TryGetValue(msg.Author.Id, out var pending))
        {
            if (pending.ExpiresUtc > DateTime.UtcNow)
                return;

            _pendingPurposePrompts.TryRemove(msg.Author.Id, out _);
        }

        _pendingPurposePrompts[msg.Author.Id] = new PendingPurposePrompt(msg.Channel.Id, DateTime.UtcNow.Add(PurposePromptLifetime));
        await _settings.MarkPurposePromptSeenAsync(msg.Author.Id);
        await ReplyToMessageAsync(msg, _bangResponses.GetPurposeQuestion());
    }

    private async Task<bool> HandlePendingPurposeReplyAsync(SocketMessage msg, string content)
    {
        if (!_pendingPurposePrompts.TryGetValue(msg.Author.Id, out var pending))
            return false;

        if (pending.ExpiresUtc <= DateTime.UtcNow)
        {
            _pendingPurposePrompts.TryRemove(msg.Author.Id, out _);
            return false;
        }

        if (pending.ChannelId != msg.Channel.Id)
            return false;

        if (!_bangResponses.LooksLikePurposeAnswer(content))
            return false;

        _pendingPurposePrompts.TryRemove(msg.Author.Id, out _);
        await ReplyToMessageAsync(msg, _bangResponses.GetPurposeCrisisResponse());
        _logger.LogInformation("Responded to purpose answer from {User}", msg.Author.Username);
        return true;
    }
}
