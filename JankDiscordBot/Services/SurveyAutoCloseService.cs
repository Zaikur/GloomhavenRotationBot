using Discord;
using Discord.WebSocket;
using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

/// <summary>
/// Background service that auto-closes surveys at their scheduled close time and posts results.
/// </summary>
public sealed class SurveyAutoCloseService : BackgroundService
{
    private readonly BotRepository _repo;
    private readonly SurveyService _surveyService;
    private readonly DiscordSocketClient _client;
    private readonly AppSettingsService _settings;
    private readonly ILogger<SurveyAutoCloseService> _log;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);

    public SurveyAutoCloseService(
        BotRepository repo,
        SurveyService surveyService,
        DiscordSocketClient client,
        AppSettingsService settings,
        ILogger<SurveyAutoCloseService> log)
    {
        _repo = repo;
        _surveyService = surveyService;
        _client = client;
        _settings = settings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("SurveyAutoCloseService started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndCloseSurveysAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error in survey auto-close check");
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("SurveyAutoCloseService stopping");
        }
    }

    private async Task CheckAndCloseSurveysAsync(CancellationToken ct)
    {
        var surveys = await _repo.GetAllSurveysAsync();
        var now = DateTime.UtcNow;

        foreach (var survey in surveys)
        {
            if (survey.Status != "Open" || now < survey.CloseAtUtc)
                continue;

            _log.LogInformation("Auto-closing survey {SurveyId}", survey.Id);

            try
            {
                await PostSurveyResultsAsync(survey, ct);
                survey.Status = "Closed";
                await _repo.UpdateSurveyAsync(survey);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to auto-close survey {SurveyId}", survey.Id);
            }
        }
    }

    private async Task PostSurveyResultsAsync(Survey survey, CancellationToken ct)
    {
        var (_, guildId, _) = await _settings.GetDiscordConfigAsync();
        var (channelId, _, _) = await _settings.GetAnnouncementConfigAsync();

        if (channelId == 0 || _client.GetGuild(guildId) == null)
            return;

        var guild = _client.GetGuild(guildId);
        var channel = guild?.GetTextChannel(channelId) as ITextChannel;
        if (channel == null)
            return;

        // Build results
        var questions = await _repo.GetQuestionsBySurveyAsync(survey.Id);
        var responses = await _repo.GetResponsesBySurveyAsync(survey.Id);
        var feedback = await _repo.GetFeedbackBySurveyAsync(survey.Id);
        var responderIds = responses.Select(r => r.UserId).Distinct().ToList();

        var embed = new EmbedBuilder()
            .WithTitle($"Survey Closed: {survey.Title}")
            .WithColor(Color.Green)
            .AddField("Total Responses", $"{responderIds.Count}/{survey.InvitedCount}", inline: true)
            .WithFooter("Survey results archived");

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

        // Generate and add hot takes
        var feedbackTexts = feedback
            .Where(f => !string.IsNullOrWhiteSpace(f.FeedbackText))
            .Select(f => f.FeedbackText)
            .ToList();

        if (feedbackTexts.Count > 0)
        {
            survey.HotTakes = await _surveyService.GenerateHotTakesAsync(feedbackTexts, ct);
            embed.AddField("Insights", survey.HotTakes, inline: false);
        }

        var msg = await channel.SendMessageAsync(embed: embed.Build());
        survey.ResultsMessageId = msg.Id;
        survey.Status = "Results Posted";
        await _repo.UpdateSurveyAsync(survey);
    }
}
