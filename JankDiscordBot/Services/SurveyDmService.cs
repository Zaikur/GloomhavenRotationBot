using Discord;
using Discord.WebSocket;
using GloomhavenRotationBot.Data;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

/// <summary>
/// Manages sending survey DMs and collecting responses via buttons and modals.
/// </summary>
public sealed class SurveyDmService
{
    private readonly DiscordSocketClient _client;
    private readonly BotRepository _repo;
    private readonly ILogger<SurveyDmService> _log;

    public SurveyDmService(DiscordSocketClient client, BotRepository repo, ILogger<SurveyDmService> log)
    {
        _client = client;
        _repo = repo;
        _log = log;
    }

    /// <summary>
    /// Sends a survey as DMs to all target members.
    /// Creates interactive buttons for each question and a feedback modal option.
    /// </summary>
    public async Task<int> SendSurveyDmsAsync(Survey survey, List<ulong> targetUserIds, CancellationToken ct = default)
    {
        var questions = await _repo.GetQuestionsBySurveyAsync(survey.Id);
        var sentCount = 0;

        foreach (var userId in targetUserIds)
        {
            try
            {
                var user = await _client.GetUserAsync(userId);
                if (user == null)
                {
                    _log.LogWarning("User {UserId} not found", userId);
                    continue;
                }

                var dmChannel = await user.CreateDMChannelAsync();

                // Send intro message
                var introEmbed = new EmbedBuilder()
                    .WithTitle(survey.Title)
                    .WithDescription(survey.Description ?? "Your response to this survey is important.")
                    .WithColor(Color.Blue)
                    .AddField("Instructions", "Select your response to each question below.")
                    .WithFooter("Survey closes " + FormatTime(survey.CloseAtUtc))
                    .Build();

                await dmChannel.SendMessageAsync(embed: introEmbed);

                // Send each question with response buttons
                int qNum = 1;
                foreach (var question in questions)
                {
                    var options = await _repo.GetOptionsByQuestionAsync(question.Id);

                    var embed = new EmbedBuilder()
                        .WithTitle($"Q{qNum}: {question.Text}")
                        .WithColor(Color.Blue)
                        .Build();

                    var componentBuilder = new ComponentBuilder();
                    foreach (var option in options)
                    {
                        componentBuilder.WithButton(
                            label: option.Text,
                            customId: $"survey_opt_{survey.Id}_{question.Id}_{option.Id}_{userId}",
                            style: ButtonStyle.Secondary
                        );
                    }

                    await dmChannel.SendMessageAsync(
                        embed: embed,
                        components: componentBuilder.Build()
                    );

                    qNum++;
                }

                // Send feedback modal instruction (using button to trigger modal)
                var feedbackButton = new ButtonBuilder()
                    .WithLabel("Add Feedback (Optional)")
                    .WithCustomId($"survey_feedback_{survey.Id}_{userId}")
                    .WithStyle(ButtonStyle.Primary);

                var feedbackComponent = new ComponentBuilder().WithButton(feedbackButton);
                var feedbackEmbed = new EmbedBuilder()
                    .WithTitle("Feedback")
                    .WithDescription("Optionally add any thoughts or feedback about this survey. Your response will be kept anonymous.")
                    .WithColor(Color.Blue)
                    .Build();

                await dmChannel.SendMessageAsync(
                    embed: feedbackEmbed,
                    components: feedbackComponent.Build()
                );

                sentCount++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send DM to user {UserId}", userId);
            }
        }

        return sentCount;
    }

    /// <summary>
    /// Records a user's response to a survey question.
    /// </summary>
    public async Task<bool> RecordResponseAsync(string surveyId, ulong userId, string questionId, string optionId, CancellationToken ct = default)
    {
        try
        {
            var response = new SurveyResponse
            {
                Id = Guid.NewGuid().ToString(),
                SurveyId = surveyId,
                UserId = userId,
                QuestionId = questionId,
                SelectedOptionId = optionId,
                SubmittedUtc = DateTime.UtcNow
            };

            await _repo.CreateResponseAsync(response);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to record response for user {UserId} survey {SurveyId}", userId, surveyId);
            return false;
        }
    }

    /// <summary>
    /// Records feedback for a survey respondent (anonymized).
    /// </summary>
    public async Task<bool> RecordFeedbackAsync(string surveyId, ulong userId, string feedbackText, CancellationToken ct = default)
    {
        try
        {
            // Check if already has feedback
            var existing = await _repo.GetFeedbackBySurveyAsync(surveyId);
            var userFeedback = existing.FirstOrDefault(f => f.UserId == userId);

            if (userFeedback != null)
            {
                // Update
                userFeedback.FeedbackText = feedbackText;
                userFeedback.SubmittedUtc = DateTime.UtcNow;
            }
            else
            {
                userFeedback = new SurveyFeedback
                {
                    Id = Guid.NewGuid().ToString(),
                    SurveyId = surveyId,
                    UserId = userId,
                    FeedbackText = feedbackText,
                    SubmittedUtc = DateTime.UtcNow
                };
            }

            await _repo.CreateFeedbackAsync(userFeedback);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to record feedback for user {UserId} survey {SurveyId}", userId, surveyId);
            return false;
        }
    }

    private static string FormatTime(DateTime utc)
    {
        var offset = new DateTimeOffset(utc, TimeSpan.Zero);
        return $"<t:{offset.ToUnixTimeSeconds()}:R>";
    }
}
