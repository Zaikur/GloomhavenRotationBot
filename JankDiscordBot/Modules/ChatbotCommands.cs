using Discord.Interactions;
using GloomhavenRotationBot.Services;

namespace GloomhavenRotationBot.Discord.Modules;

public sealed class ChatbotCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ChatbotService _chatbot;

    public ChatbotCommands(ChatbotService chatbot)
    {
        _chatbot = chatbot;
    }

    [SlashCommand("chatbot", "Control the chatbot auto-responses")]
    public async Task ChatbotAsync(
        [Summary(description: "Action: pause, resume, or status")] string action,
        [Summary(description: "Duration in minutes (for pause only)")] int minutes = 60)
    {
        await DeferAsync(ephemeral: true);

        var actionLower = action.ToLowerInvariant();

        switch (actionLower)
        {
            case "pause":
                if (minutes < 1 || minutes > 1440)
                {
                    await FollowupAsync("Duration must be between 1 and 1440 minutes (24 hours).", ephemeral: true);
                    return;
                }

                _chatbot.Pause(TimeSpan.FromMinutes(minutes));
                await FollowupAsync($"🔕 Chatbot paused for **{minutes} minute(s)**.", ephemeral: true);
                break;

            case "resume":
                _chatbot.Resume();
                await FollowupAsync("🔔 Chatbot resumed and will respond to questions.", ephemeral: true);
                break;

            case "status":
                if (_chatbot.IsPaused())
                {
                    var remaining = _chatbot.GetPauseTimeRemaining();
                    var mins = remaining?.TotalMinutes ?? 0;
                    await FollowupAsync($"🔕 Chatbot is **paused** for {mins:F0} more minute(s).", ephemeral: true);
                }
                else
                {
                    await FollowupAsync("🔔 Chatbot is **active** and responding to questions.", ephemeral: true);
                }
                break;

            default:
                await FollowupAsync("Action must be `pause`, `resume`, or `status`.", ephemeral: true);
                break;
        }
    }
}
