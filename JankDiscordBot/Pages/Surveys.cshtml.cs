using GloomhavenRotationBot.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GloomhavenRotationBot.Pages;

public class SurveysModel : PageModel
{
    private readonly BotRepository _repo;

    public List<Survey> Surveys { get; set; } = new();
    public Dictionary<string, int> ResponseCounts { get; set; } = new();

    public SurveysModel(BotRepository repo)
    {
        _repo = repo;
    }

    public async Task OnGetAsync()
    {
        Surveys = await _repo.GetAllSurveysAsync();

        // Fetch response counts for each survey
        foreach (var survey in Surveys)
        {
            var responders = await _repo.GetSurveyRespondersAsync(survey.Id);
            ResponseCounts[survey.Id] = responders.Count;
        }
    }
}
