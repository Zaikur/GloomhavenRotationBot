using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Modules;

[Group("survey", "Manage surveys and polls")]
public sealed class SurveyCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly SurveyService _surveyService;
    private readonly SurveyDmService _surveyDm;
    private readonly BotRepository _repo;
    private readonly AppSettingsService _settings;
    private readonly GuildMemberDirectory _members;
    private readonly ILogger<SurveyCommands> _log;

    public SurveyCommands(
        SurveyService surveyService,
        SurveyDmService surveyDm,
        BotRepository repo,
        AppSettingsService settings,
        GuildMemberDirectory members,
        ILogger<SurveyCommands> log)
    {
        _surveyService = surveyService;
        _surveyDm = surveyDm;
        _repo = repo;
        _settings = settings;
        _members = members;
        _log = log;
    }

    [SlashCommand("create", "Create a new survey")]
    public async Task CreateSurvey(
        [Summary(description: "The survey topic or prompt")] string topic,
        [Summary(description: "Optional description")] string? description = null)
    {
        await DeferAsync(ephemeral: true);

        try
        {
            // Generate questions
            var generated = await _surveyService.GenerateQuestionsAsync(topic);

            // Show preview with confirm/cancel buttons
            var embed = new EmbedBuilder()
                .WithTitle("Survey Preview")
                .WithDescription(topic)
                .WithColor(Color.Blue)
                .AddField("Questions Generated", $"{generated.Questions.Count} question(s)")
                .WithFooter("Review and confirm to send to members");

            int qNum = 1;
            foreach (var q in generated.Questions)
            {
                var opts = string.Join(", ", q.Options);
                embed.AddField($"Q{qNum}: {q.Text}", opts, inline: false);
                qNum++;
            }

            var button1 = new ButtonBuilder()
                .WithLabel("Confirm & Send")
                .WithStyle(ButtonStyle.Success)
                .WithCustomId($"survey_confirm_{Context.User.Id}");

            var button2 = new ButtonBuilder()
                .WithLabel("Cancel")
                .WithStyle(ButtonStyle.Danger)
                .WithCustomId($"survey_cancel_{Context.User.Id}");

            var component = new ComponentBuilder()
                .WithButton(button1)
                .WithButton(button2);

            // Store the generated survey temporarily (could use cache or session)
            // For now, we'll use a static cache (in production, use proper session management)
            SurveyPreviewCache.Set(Context.User.Id, new SurveyPreview
            {
                Topic = topic,
                Description = description,
                Generated = generated,
                TargetUsers = (await _members.GetMembersAsync()).Select(m => m.Id).ToList(),
                CreatedAtUtc = DateTime.UtcNow
            });

            await FollowupAsync(
                embed: embed.Build(),
                components: component.Build(),
                ephemeral: true
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create survey");
            await FollowupAsync("Failed to generate survey questions. Please try again.", ephemeral: true);
        }
    }

    public async Task SendSurveyDmsAsync(Survey survey, List<ulong> targetUserIds)
    {
        _log.LogInformation("Sending survey {SurveyId} DMs to {Count} members", survey.Id, targetUserIds.Count);
        
        var sentCount = await _surveyDm.SendSurveyDmsAsync(survey, targetUserIds);
        survey.InvitedCount = targetUserIds.Count;
        await _repo.UpdateSurveyAsync(survey);

        _log.LogInformation("Survey {SurveyId} DMs sent to {SentCount} members", survey.Id, sentCount);
    }

    [SlashCommand("close", "Close an open survey and post results")]
    public async Task CloseSurvey(
        [Summary(description: "Survey ID or partial match")] string surveyId)
    {
        await DeferAsync(ephemeral: true);

        try
        {
            var survey = await _repo.GetSurveyAsync(surveyId);
            if (survey == null)
            {
                await FollowupAsync("Survey not found.", ephemeral: true);
                return;
            }

            if (survey.Status != "Open")
            {
                await FollowupAsync("Survey is not open.", ephemeral: true);
                return;
            }

            // Close and post results
            await PostSurveyResultsAsync(survey);

            survey.Status = "Closed";
            await _repo.UpdateSurveyAsync(survey);

            await FollowupAsync($"Survey closed and results posted to the announcement channel.", ephemeral: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to close survey");
            await FollowupAsync("Failed to close survey.", ephemeral: true);
        }
    }

    [SlashCommand("list", "List recent surveys")]
    public async Task ListSurveys([Summary(description: "Show closed surveys too")] bool includeClosed = false)
    {
        await DeferAsync(ephemeral: true);

        try
        {
            var all = await _repo.GetAllSurveysAsync();
            var filtered = includeClosed ? all : all.Where(s => s.Status == "Open").ToList();

            if (filtered.Count == 0)
            {
                await FollowupAsync("No surveys found.", ephemeral: true);
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("Surveys")
                .WithColor(Color.Blue);

            foreach (var survey in filtered.Take(10))
            {
                var responders = await _repo.GetSurveyRespondersAsync(survey.Id);
                embed.AddField(
                    survey.Title,
                    $"**Status:** {survey.Status}\n" +
                    $"**Responses:** {responders.Count}/{survey.InvitedCount}\n" +
                    $"**Created:** <t:{((DateTimeOffset)survey.CreatedUtc).ToUnixTimeSeconds()}:R>\n" +
                    $"**ID:** `{survey.Id[..8]}`",
                    inline: false
                );
            }

            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to list surveys");
            await FollowupAsync("Failed to list surveys.", ephemeral: true);
        }
    }

    private async Task PostSurveyResultsAsync(Survey survey)
    {
        var (_, guildId, _) = await _settings.GetDiscordConfigAsync();
        var (channelId, _, _) = await _settings.GetAnnouncementConfigAsync();

        if (channelId == 0)
        {
            _log.LogWarning("No announcement channel configured");
            return;
        }

        try
        {
            var guild = Context.Client.GetGuild(guildId);
            var channel = guild?.GetTextChannel(channelId) as ITextChannel;
            if (channel == null)
            {
                _log.LogWarning("Announcement channel not found");
                return;
            }

            // Build results embed
            var questions = await _repo.GetQuestionsBySurveyAsync(survey.Id);
            var responses = await _repo.GetResponsesBySurveyAsync(survey.Id);
            var feedback = await _repo.GetFeedbackBySurveyAsync(survey.Id);
            var responderIds = responses.Select(r => r.UserId).Distinct().ToList();

            var embed = new EmbedBuilder()
                .WithTitle($"Survey Results: {survey.Title}")
                .WithColor(Color.Green)
                .AddField("Total Responses", $"{responderIds.Count}/{survey.InvitedCount}", inline: true);

            foreach (var question in questions)
            {
                var options = await _repo.GetOptionsByQuestionAsync(question.Id);
                var qResponses = responses.Where(r => r.QuestionId == question.Id).ToList();

                var optionsSummary = options
                    .OrderByDescending(o => o.ResponseCount)
                    .Select(o =>
                    {
                        var pct = qResponses.Count > 0 ? (o.ResponseCount * 100.0 / qResponses.Count) : 0;
                        return $"{o.Text}: {o.ResponseCount} ({pct:F0}%)";
                    })
                    .ToList();

                embed.AddField(
                    question.Text,
                    string.Join("\n", optionsSummary),
                    inline: false
                );
            }

            // Add hot takes if available
            if (!string.IsNullOrWhiteSpace(survey.HotTakes))
            {
                embed.AddField("Insights", survey.HotTakes, inline: false);
            }
            else if (feedback.Count > 0)
            {
                // Generate hot takes now if not already done
                var feedbackTexts = feedback
                    .Where(f => !string.IsNullOrWhiteSpace(f.FeedbackText))
                    .Select(f => f.FeedbackText)
                    .ToList();

                if (feedbackTexts.Count > 0)
                {
                    survey.HotTakes = await _surveyService.GenerateHotTakesAsync(feedbackTexts);
                    embed.AddField("Insights", survey.HotTakes, inline: false);
                }
            }

            var msg = await channel.SendMessageAsync(embed: embed.Build());
            survey.ResultsMessageId = msg.Id;
            survey.Status = "Results Posted";
            await _repo.UpdateSurveyAsync(survey);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to post survey results");
        }
    }
}

/// <summary>
/// Temporary in-memory cache for survey previews during creation.
/// In production, consider using a distributed cache or database.
/// </summary>
public static class SurveyPreviewCache
{
    private static readonly Dictionary<ulong, SurveyPreview> Cache = new();

    public static void Set(ulong userId, SurveyPreview preview)
    {
        Cache[userId] = preview;
    }

    public static SurveyPreview? Get(ulong userId)
    {
        return Cache.TryGetValue(userId, out var preview) && DateTime.UtcNow - preview.CreatedAtUtc < TimeSpan.FromMinutes(5)
            ? preview
            : null;
    }

    public static void Remove(ulong userId)
    {
        Cache.Remove(userId);
    }
}

public sealed class SurveyPreview
{
    public string Topic { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SurveyGenerationResult Generated { get; set; } = new();
    public List<ulong> TargetUsers { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
}
