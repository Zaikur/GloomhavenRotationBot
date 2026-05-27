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
    private static readonly TimeSpan BirthdayTimeoutDuration = TimeSpan.FromMinutes(5);

    private readonly DiscordSocketClient _client;
    private readonly BangResponseService _bangResponses;
    private readonly ChatbotService _chatbot;
    private readonly AppSettingsService _settings;
    private readonly ILogger<MessageHandler> _logger;
    private readonly ConcurrentDictionary<ulong, PendingPurposePrompt> _pendingPurposePrompts = new();
    private readonly ConcurrentDictionary<ulong, BirthdayTimeout> _birthdayTimeouts = new();

    private sealed record PendingPurposePrompt(ulong ChannelId, DateTime ExpiresUtc);
    private sealed record BirthdayTimeout(DateTime ExpiresUtc, int ViolationsSinceLastEscalation, int EscalationLevel);

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

            // Check if user is in birthday timeout
            if (_birthdayTimeouts.TryGetValue(msg.Author.Id, out var timeout))
            {
                if (timeout.ExpiresUtc > DateTime.UtcNow)
                {
                    var timeRemaining = timeout.ExpiresUtc - DateTime.UtcNow;
                    var newViolationCount = timeout.ViolationsSinceLastEscalation + 1;
                    var violationsUntilEscalation = 3;
                    var isTimeoutInsult = _bangResponses.LooksLikeTimeoutInsult(content, _client.CurrentUser.Id, _client.CurrentUser.Username);
                    
                    string timeoutResponseMsg;
                    DateTime? newExpiry = null;
                    int? newEscalationLevel = null;
                    
                    if (newViolationCount == violationsUntilEscalation)
                    {
                        // Final warning before escalation
                        var nextExtensionMinutes = 5 * (timeout.EscalationLevel + 1);
                        if (isTimeoutInsult)
                        {
                            timeoutResponseMsg = $"{_bangResponses.GetTimeoutInsultResponse(msg.Author.Username)} One more message and I'll add {nextExtensionMinutes} more minutes! ⏰";
                        }
                        else
                        {
                            timeoutResponseMsg = $"Stop messaging me, {msg.Author.Username}! You're still in timeout for {FormatTimeRemaining(timeRemaining)}. One more message and I'll add {nextExtensionMinutes} more minutes! ⏰";
                        }
                        _birthdayTimeouts[msg.Author.Id] = new BirthdayTimeout(timeout.ExpiresUtc, newViolationCount, timeout.EscalationLevel);
                    }
                    else if (newViolationCount > violationsUntilEscalation)
                    {
                        // Escalate: extend timeout and increment escalation level
                        var extensionMinutes = 5 * (timeout.EscalationLevel + 1);
                        newExpiry = timeout.ExpiresUtc.AddMinutes(extensionMinutes);
                        newEscalationLevel = timeout.EscalationLevel + 1;
                        if (isTimeoutInsult)
                        {
                            timeoutResponseMsg = $"{_bangResponses.GetTimeoutInsultResponse(msg.Author.Username)} {extensionMinutes} more minutes added. You now have {FormatTimeRemaining(newExpiry.Value - DateTime.UtcNow)} left. 😤";
                        }
                        else
                        {
                            timeoutResponseMsg = $"That's it! {extensionMinutes} more minutes added to your timeout. You now have {FormatTimeRemaining(newExpiry.Value - DateTime.UtcNow)} left. This is your last warning! 😤";
                        }
                        _birthdayTimeouts[msg.Author.Id] = new BirthdayTimeout(newExpiry.Value, 0, newEscalationLevel.Value);
                    }
                    else
                    {
                        // Early violations - just remind
                        if (isTimeoutInsult)
                        {
                            timeoutResponseMsg = _bangResponses.GetTimeoutInsultResponse(msg.Author.Username);
                        }
                        else
                        {
                            timeoutResponseMsg = $"You're still in timeout for {FormatTimeRemaining(timeRemaining)}. 🕐";
                        }
                        _birthdayTimeouts[msg.Author.Id] = new BirthdayTimeout(timeout.ExpiresUtc, newViolationCount, timeout.EscalationLevel);
                    }
                    
                    await ReplyToMessageAsync(msg, timeoutResponseMsg);
                    _logger.LogInformation("User {User} attempted to message while in birthday timeout (violation {Count}, escalation {Level}, insult: {IsInsult})", msg.Author.Username, newViolationCount, timeout.EscalationLevel, isTimeoutInsult);
                    return;
                }
                else
                {
                    _birthdayTimeouts.TryRemove(msg.Author.Id, out _);
                }
            }

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
                    var (prompt, rollResult) = await _bangResponses.GetBirthdayRollAsync(msg.Author.Id, msg.Author.Username);
                    await msg.Channel.SendMessageAsync(prompt);
                    
                    // If there's a roll result (Roll > 0), add a delay and send the result
                    if (rollResult.Roll > 0)
                    {
                        await Task.Delay(1000); // 1 second delay
                        var resultMsg = $"🎲 You rolled a **{rollResult.Roll}**!";
                        if (rollResult.Response != null)
                        {
                            resultMsg += $" {rollResult.Response}";
                            
                            // If it's a critical fail (1-5), put user in timeout
                            if (rollResult.Roll <= 5)
                            {
                                _birthdayTimeouts[msg.Author.Id] = new BirthdayTimeout(DateTime.UtcNow.Add(BirthdayTimeoutDuration), 0, 0);
                                _logger.LogInformation("User {User} rolled {Roll} and entered birthday timeout", msg.Author.Username, rollResult.Roll);
                            }
                        }
                        await msg.Channel.SendMessageAsync(resultMsg);
                    }
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

    private static string FormatTimeRemaining(TimeSpan timeRemaining)
    {
        if (timeRemaining.TotalSeconds < 60)
        {
            return $"{(int)timeRemaining.TotalSeconds}s";
        }
        
        if (timeRemaining.TotalMinutes < 60)
        {
            var minutes = (int)timeRemaining.TotalMinutes;
            var seconds = timeRemaining.Seconds;
            return $"{minutes}m {seconds}s";
        }

        var hours = (int)timeRemaining.TotalHours;
        var mins = timeRemaining.Minutes;
        return $"{hours}h {mins}m";
    }
}
