using System.Text.Json;
using GloomhavenRotationBot.Data;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

/// <summary>
/// Handles survey generation, parsing, and result aggregation using AI.
/// </summary>
public sealed class SurveyService
{
    private readonly AiTextService _ai;
    private readonly BotRepository _repo;
    private readonly ILogger<SurveyService> _log;

    public SurveyService(AiTextService ai, BotRepository repo, ILogger<SurveyService> log)
    {
        _ai = ai;
        _repo = repo;
        _log = log;
    }

    /// <summary>
    /// Generates survey questions and options from a user prompt.
    /// Returns a structured object with up to 4 questions, each with up to 3 options.
    /// </summary>
    public async Task<SurveyGenerationResult> GenerateQuestionsAsync(
        string userPrompt,
        CancellationToken ct = default)
    {
        var systemPrompt = @"You are a survey design expert. Generate a structured JSON survey with the following constraints:
- Maximum 4 questions
- Each question has 2-3 answer options
- Questions are clear and unambiguous
- Options are mutually exclusive
- Return ONLY valid JSON, no extra text

Format:
{
  ""questions"": [
    {
      ""text"": ""Question here?"",
      ""options"": [""Option 1"", ""Option 2"", ""Option 3""]
    }
  ]
}";

        var userMsg = $@"Create a survey for: {userPrompt}

Generate the questions and options as JSON only. No markdown, no explanation.";

        var response = await _ai.GenerateAsync(
            systemPrompt,
            userMsg,
            fallback: @"{""questions"": [{""text"": ""Did you enjoy the session?"", ""options"": [""Yes"", ""No""]}]}",
            temperature: 0.7f,
            maxTokens: 512,
            ct: ct
        );

        return ParseGenerationResult(response);
    }

    /// <summary>
    /// Generates AI "hot takes" from survey feedback.
    /// Summarizes and extracts interesting insights without revealing user identities.
    /// </summary>
    public async Task<string> GenerateHotTakesAsync(
        List<string> feedbackItems,
        CancellationToken ct = default)
    {
        if (feedbackItems.Count == 0)
            return "No feedback provided.";

        var feedbackText = string.Join("\n\n", feedbackItems.Take(20)); // Limit to avoid huge tokens
        var systemPrompt = @"You are a witty analyst. Summarize the following feedback into 2-3 punchy insights.
- Keep it light and entertaining
- Extract actual themes, don't make assumptions
- Never mention names or identities
- Keep it under 150 words
- Use a conversational tone";

        var userMsg = $@"Here's feedback from a survey:

{feedbackText}

Generate 2-3 hot takes from this feedback.";

        var response = await _ai.GenerateAsync(
            systemPrompt,
            userMsg,
            fallback: "The feedback shows a range of perspectives on this topic.",
            temperature: 0.8f,
            maxTokens: 256,
            ct: ct
        );

        return response;
    }

    private SurveyGenerationResult ParseGenerationResult(string jsonResponse)
    {
        try
        {
            var trimmed = jsonResponse
                .TrimStart()
                .TrimEnd()
                .RemoveErrorTag();

            // Find JSON object start
            var startIdx = trimmed.IndexOf('{');
            var endIdx = trimmed.LastIndexOf('}');

            if (startIdx < 0 || endIdx < 0 || startIdx >= endIdx)
                return SurveyGenerationResult.Default();

            var jsonStr = trimmed[startIdx..(endIdx + 1)];
            var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            if (!root.TryGetProperty("questions", out var questionsArray))
                return SurveyGenerationResult.Default();

            var questions = new List<SurveyGenerationQuestion>();

            foreach (var q in questionsArray.EnumerateArray())
            {
                if (!q.TryGetProperty("text", out var textEl) || !q.TryGetProperty("options", out var optionsEl))
                    continue;

                var text = textEl.GetString()?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                var options = new List<string>();
                foreach (var opt in optionsEl.EnumerateArray())
                {
                    var optText = opt.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(optText) && options.Count < 3)
                        options.Add(optText);
                }

                if (options.Count >= 2)
                {
                    questions.Add(new SurveyGenerationQuestion
                    {
                        Text = text,
                        Options = options
                    });
                }

                if (questions.Count >= 4)
                    break;
            }

            if (questions.Count == 0)
                return SurveyGenerationResult.Default();

            return new SurveyGenerationResult { Questions = questions };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI survey response; returning default");
            return SurveyGenerationResult.Default();
        }
    }
}

public sealed class SurveyGenerationResult
{
    public List<SurveyGenerationQuestion> Questions { get; set; } = new();

    public static SurveyGenerationResult Default()
    {
        return new()
        {
            Questions = new()
            {
                new()
                {
                    Text = "Did you enjoy the session?",
                    Options = new() { "Yes", "No" }
                }
            }
        };
    }
}

public sealed class SurveyGenerationQuestion
{
    public string Text { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
}

public static class SurveyResponseExtensions
{
    public static string RemoveErrorTag(this string text)
    {
        // Remove error tags like [AI-E02]
        var idx = text.LastIndexOf('[');
        if (idx > 0 && text.EndsWith(']'))
            return text[..idx].TrimEnd();
        return text;
    }
}
