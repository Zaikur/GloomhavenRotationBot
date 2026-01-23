using GloomhavenRotationBot.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GloomhavenRotationBot.Pages;

public class SurveyDetailsModel : PageModel
{
    private readonly BotRepository _repo;

    public Survey? Survey { get; set; }
    public List<SurveyQuestion> Questions { get; set; } = new();
    public Dictionary<string, List<SurveyOption>> OptionsByQuestion { get; set; } = new();
    public Dictionary<string, List<SurveyResponse>> ResponsesByQuestion { get; set; } = new();
    public List<SurveyFeedback> Feedback { get; set; } = new();
    public List<ulong> Responders { get; set; } = new();

    public SurveyDetailsModel(BotRepository repo)
    {
        _repo = repo;
    }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        Survey = await _repo.GetSurveyAsync(id);
        if (Survey == null)
            return NotFound();

        Questions = await _repo.GetQuestionsBySurveyAsync(id);
        Feedback = await _repo.GetFeedbackBySurveyAsync(id);

        var allResponses = await _repo.GetResponsesBySurveyAsync(id);
        Responders = allResponses.Select(r => r.UserId).Distinct().ToList();

        // Organize by question
        foreach (var question in Questions)
        {
            var options = await _repo.GetOptionsByQuestionAsync(question.Id);
            OptionsByQuestion[question.Id] = options;

            var qResponses = allResponses.Where(r => r.QuestionId == question.Id).ToList();
            ResponsesByQuestion[question.Id] = qResponses;
        }

        return Page();
    }
}
