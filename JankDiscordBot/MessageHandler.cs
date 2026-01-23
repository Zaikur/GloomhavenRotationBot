using Discord;
using Discord.WebSocket;
using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot;

/// <summary>
/// Handles incoming Discord messages and routes them to the chatbot service.
/// Also handles survey button interactions and modals.
/// </summary>
public sealed class MessageHandler
{
    private readonly DiscordSocketClient _client;
    private readonly ChatbotService _chatbot;
    private readonly AppSettingsService _settings;
    private readonly SurveyDmService _surveyDm;
    private readonly BotRepository _repo;
    private readonly ILogger<MessageHandler> _logger;

    public MessageHandler(
        DiscordSocketClient client,
        ChatbotService chatbot,
        AppSettingsService settings,
        SurveyDmService surveyDm,
        BotRepository repo,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _chatbot = chatbot;
        _settings = settings;
        _surveyDm = surveyDm;
        _repo = repo;
        _logger = logger;
    }

    public Task InitializeAsync()
    {
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
        _client.ModalSubmitted += OnModalSubmittedAsync;
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

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        try
        {
            var customId = component.Data.CustomId;

            // Handle survey option selection: survey_opt_{surveyId}_{questionId}_{optionId}_{userId}
            if (customId.StartsWith("survey_opt_"))
            {
                var parts = customId.Split('_');
                if (parts.Length == 8 && ulong.TryParse(parts[7], out var userId) &&
                    userId == component.User.Id)
                {
                    var surveyId = parts[2];
                    var questionId = parts[3];
                    var optionId = parts[4];

                    var success = await _surveyDm.RecordResponseAsync(surveyId, userId, questionId, optionId);
                    if (success)
                    {
                        await component.RespondAsync("✓ Response recorded!", ephemeral: true);
                    }
                    else
                    {
                        await component.RespondAsync("Failed to record response.", ephemeral: true);
                    }
                }
                else
                {
                    await component.RespondAsync("This survey is not for you.", ephemeral: true);
                }
            }
            // Handle feedback modal trigger: survey_feedback_{surveyId}_{userId}
            else if (customId.StartsWith("survey_feedback_"))
            {
                var parts = customId.Split('_');
                if (parts.Length == 5 && ulong.TryParse(parts[3], out var userId) &&
                    userId == component.User.Id)
                {
                    var surveyId = parts[2];
                    var modal = new ModalBuilder()
                        .WithTitle("Survey Feedback")
                        .WithCustomId($"survey_feedback_submit_{surveyId}_{userId}")
                        .AddTextInput("Feedback", "feedback_text", placeholder: "Share your thoughts (optional)", style: TextInputStyle.Paragraph, required: false)
                        .Build();

                    await component.RespondWithModalAsync(modal);
                }
                else
                {
                    await component.RespondAsync("This survey is not for you.", ephemeral: true);
                }
            }
            // Handle survey confirm: survey_confirm_{userId}
            else if (customId.StartsWith("survey_confirm_") && ulong.TryParse(customId.Split('_')[2], out var confirmUserId) &&
                     confirmUserId == component.User.Id)
            {
                await HandleSurveyConfirmAsync(component);
            }
            // Handle survey cancel: survey_cancel_{userId}
            else if (customId.StartsWith("survey_cancel_") && ulong.TryParse(customId.Split('_')[2], out var cancelUserId) &&
                     cancelUserId == component.User.Id)
            {
                await component.RespondAsync("Survey creation cancelled.", ephemeral: true);
                Modules.SurveyPreviewCache.Remove(component.User.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling button interaction");
            try { await component.RespondAsync("An error occurred.", ephemeral: true); } catch { }
        }
    }

    private async Task HandleSurveyConfirmAsync(SocketMessageComponent component)
    {
        try
        {
            var preview = Modules.SurveyPreviewCache.Get(component.User.Id);
            if (preview == null)
            {
                await component.RespondAsync("Survey preview expired. Please create again.", ephemeral: true);
                return;
            }

            await component.DeferAsync(ephemeral: true);

            // Create survey in database
            var survey = new Survey
            {
                Id = Guid.NewGuid().ToString(),
                Title = preview.Topic,
                Description = preview.Description,
                CreatedByUserId = component.User.Id,
                CreatedUtc = DateTime.UtcNow,
                CloseAtUtc = DateTime.UtcNow.AddHours(24),
                Status = "Open",
                InvitedCount = preview.TargetUsers.Count
            };

            await _repo.CreateSurveyAsync(survey);

            // Create questions and options
            int qOrder = 0;
            foreach (var genQuestion in preview.Generated.Questions)
            {
                var question = new SurveyQuestion
                {
                    Id = Guid.NewGuid().ToString(),
                    SurveyId = survey.Id,
                    Order = qOrder,
                    Text = genQuestion.Text
                };
                await _repo.CreateQuestionAsync(question);

                int oOrder = 0;
                foreach (var optText in genQuestion.Options)
                {
                    var option = new SurveyOption
                    {
                        Id = Guid.NewGuid().ToString(),
                        QuestionId = question.Id,
                        Order = oOrder,
                        Text = optText
                    };
                    await _repo.CreateOptionAsync(option);
                    oOrder++;
                }

                qOrder++;
            }

            _logger.LogInformation("Sending survey DMs for survey {SurveyId} to {Count} members", survey.Id, preview.TargetUsers.Count);

            // Send DMs
            _ = Task.Run(async () =>
            {
                try
                {
                    var sentCount = await _surveyDm.SendSurveyDmsAsync(survey, preview.TargetUsers);
                    survey.InvitedCount = preview.TargetUsers.Count;
                    await _repo.UpdateSurveyAsync(survey);
                    _logger.LogInformation("Survey {SurveyId} DMs sent to {SentCount}/{Total} members", 
                        survey.Id, sentCount, preview.TargetUsers.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send survey DMs for {SurveyId}", survey.Id);
                }
            });

            await component.FollowupAsync(
                $"Survey created! DMs are being sent to {preview.TargetUsers.Count} members. " +
                $"Survey will close in 24 hours.",
                ephemeral: true
            );

            Modules.SurveyPreviewCache.Remove(component.User.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error confirming survey");
            await component.FollowupAsync("Failed to create survey.", ephemeral: true);
        }
    }

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        try
        {
            var customId = modal.Data.CustomId;

            // Handle feedback submission: survey_feedback_submit_{surveyId}_{userId}
            if (customId.StartsWith("survey_feedback_submit_"))
            {
                var parts = customId.Split('_');
                if (parts.Length == 6 && ulong.TryParse(parts[4], out var userId) &&
                    userId == modal.User.Id)
                {
                    var surveyId = parts[3];
                    var feedbackInput = modal.Data.Components.FirstOrDefault(c => c.CustomId == "feedback_text");
                    var feedbackText = feedbackInput?.Value ?? "";

                    if (!string.IsNullOrWhiteSpace(feedbackText))
                    {
                        var success = await _surveyDm.RecordFeedbackAsync(surveyId, userId, feedbackText);
                        if (success)
                        {
                            await modal.RespondAsync("✓ Feedback saved! Thank you.", ephemeral: true);
                        }
                        else
                        {
                            await modal.RespondAsync("Failed to save feedback.", ephemeral: true);
                        }
                    }
                    else
                    {
                        await modal.RespondAsync("Skipped feedback.", ephemeral: true);
                    }
                }
                else
                {
                    await modal.RespondAsync("This survey is not for you.", ephemeral: true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling modal submission");
            try { await modal.RespondAsync("An error occurred.", ephemeral: true); } catch { }
        }
    }
}
