using System.Text;
using System.Text.RegularExpressions;
using GloomhavenRotationBot.Data;

namespace GloomhavenRotationBot.Services;

public sealed class BangResponseService
{
    private const int PurposePromptChanceDenominator = 12;

    private static readonly string[] BirthdayDayResponses =
    {
        "🎉🎂 Happy Birthday, {0}! It is officially the big day! 🎂🎉",
        "🚨🎂 Cake alarm: it is officially {0}'s birthday! Everybody remain appropriately hyped! 🎉",
        "📣 It is {0}'s actual birthday today! This is not a drill! 🥳🎈",
        "🎈 Happy birthday, {0}!!!!! 🎉✨"
    };

    private static readonly string[] BirthdayWeekResponses =
    {
        "🎉 Happy birthday week, {0}! The countdown is on. 🎂",
        "🥳 We are officially in {0}'s birthday week, and that deserves some excitement.",
        "Happy birthday week, {0}! 🎈 Your big day is getting close.",
        "✨ It is your birthday week, {0}, so early celebration mode is now active. ✨"
    };

    private static readonly string[] BirthdayMonthResponses =
    {
        "🗓️ Apparently it is your birthday month, {0}. Fine. Happy birthday month. 🎉",
        "🥳 I have been informed that {0} is in their birthday month. Congrats, I guess. 🎈",
        "🎉 Happy birthday month, {0}. This seems excessive, but here we are. ✨",
        "🎂 It is technically your birthday month, {0}, so accept this begrudging recognition."
    };

    private static readonly string[] BirthdayRollPrompts =
    {
        "Hmmm... It's not your birthday month. Tell you what, we'll roll for it. 🎲",
        "Not your birthday month. But hey, let's see if the RNG gods are feeling generous. 🎲",
        "I see what you're doing. It's not your birthday month. Wanna roll for it though? 🎲",
        "Nice try. Not your birthday month. But I'll let you roll the dice on it. 🎲",
        "Nope, not your month. But you know what? Let's gamble. Roll for it. 🎲"
    };

    private static readonly string[] BirthdayRollCritFailResponses =
    {
        "GLOM is so sorry, {0}. The dice said no birthday, and GLOM must listen to the dice. 🎲",
        "GLOM wanted this to be your bonus birthday, {0}. GLOM truly did. But the dice have said no. 🎂",
        "GLOM has so many sorries to give you, {0}. Unfortunately, none of them can turn this roll into a birthday. 🎲",
        "Please forgive GLOM, {0}. GLOM checked the roll twice, but your emergency birthday application was denied. 🎂",
        "GLOM brings terrible news, {0}. Today is not your bonus birthday. GLOM is giving you all of his apologies. 🎲"
    };

    private static readonly string[] BirthdayRollNeutralResponses =
    {
        "Nope. Not today.",
        "The dice have spoken. It's a no.",
        "Better luck next time, {0}.",
        "Not in the cards for you right now.",
        "The birthday gods have rejected {0}'s petition.",
        "The RNG has decided: hard pass.",
        "Close, but not close enough.",
        "The universe says no thank you.",
        "Not quite, {0}. Keep it moving."
    };

    private static readonly string[] BirthdayRollSuccessResponses =
    {
        "🎉🎂 WHOA! The dice gods have spoken! Congratulations, {0}! You somehow have a birthday today! 🎂🎉",
        "🚨🎂 JACKPOT! Against all odds, {0} has won themselves a birthday! Everybody celebrate! 🎉",
        "📣 INCREDIBLE! The RNG has blessed {0}! It is now officially their birthday! 🥳🎈",
        "🎈 BY THE GRACE OF THE DICE! Happy magical birthday, {0}! 🎉✨",
        "🎯 CRITICAL SUCCESS! The birthday gods have smiled upon {0}! Happy birthday! 🎂🎉"
    };

    private static readonly string[] BeanFacts =
    {
        "Jelly beans were once marketed as a candy you could eat year-round instead of only at Easter.",
        "Mexican jumping beans are not beans at all; they move because a moth larva is living inside the seed pod.",
        "A bean seed can split a sidewalk crack wider over time if the plant keeps growing in the right spot.",
        "Dry beans can get dramatically heavier after soaking because they pull water deep into the seed.",
        "Some bean flowers are bright enough that people grow the plants just for the blooms.",
        "Scarlet runner beans can make vivid red flowers before they ever give you beans.",
        "Yardlong beans are real, but they usually stop short of a full yard.",
        "The shiny spot on a bean seed is called the hilum, which is where it was attached inside the pod.",
        "A single bean plant can make dozens of pods if it stays healthy and keeps getting picked.",
        "Bean leaves can fold or droop dramatically in heat and then perk back up when conditions improve.",
        "Some old bean varieties have names that sound made up, like Dragon Tongue and Good Mother Stallard.",
        "Castor beans are called beans even though they are extremely toxic and not food.",
        "Vanilla beans come from orchids, which is a wildly fancy origin story for something called a bean.",
        "Coffee beans are actually seeds from a fruit, which means coffee starts closer to produce than people act like it does.",
        "Sea beans can drift across oceans for months before washing up on beaches.",
        "Bean weevils can emerge from stored dry beans if the beans were infested before packaging.",
        "Beanbags were originally filled with dried beans, which is exactly why they are called beanbags.",
        "Some bean pods squeak when they are fresh enough, which is a very strange vegetable flex.",
        "Dry beans can stay viable for planting much longer than most people expect if they are kept cool and dry.",
        "A bean sprout can shove a surprising amount of weight upward while it is trying to reach light.",
        "The mottled pattern on pinto beans fades as they cook, so they literally lose their spots in the pot.",
        "Black bean cooking water can turn deep purple before it darkens further in the pot.",
        "Some bean pods are fuzzy, some are smooth, and some look like they were designed by somebody holding a grudge.",
        "Runner bean vines can climb fast enough that people use them as seasonal privacy screens.",
        "The inside of a bean flower is built so pollination can happen with a surprisingly efficient little snap mechanism.",
        "Bean roots can host bacteria in little nodules, so the plant is basically growing its own underground support staff.",
        "Lima beans can be tiny, huge, pale, or speckled enough to look fake.",
        "There are bean varieties bred specifically so the pods are purple even though they often turn green when cooked.",
        "Some heirloom beans keep names from families and farms that have been passing them around for generations.",
        "A lot of canned baked beans start as navy beans because they hold together well under long cooking."
    };

    private static readonly string[] BangTopicResponses =
    {
        "🤔 Have you always liked {0}, or was it an acquired taste?",
        "😄 Has {0} always been your thing, or did you warm up to it over time?",
        "👀 Was {0} an immediate win for you, or was it more of an acquired taste situation?",
        "✨ Did {0} click right away for you, or did it grow on you?",
        "😎 Have you always been a {0} kinda guy?",
        "🎢 Were you into {0} from the start, or was that a journey?",
        "🫡 Was {0} always in your corner, or did you need time to appreciate it?"
    };

    private static readonly string[] BotInsultResponses =
    {
        "GLOM has so many sorries to give, and GLOM is giving you all of them.",
        "GLOM hears that you are upset. GLOM is very sorry, and GLOM will try to do better.",
        "GLOM did not mean to make you angry. Please accept this apology and all the extra apologies GLOM brought with him.",
        "Your feelings are large, and GLOM's pile of sorries is even larger. They are all yours.",
        "GLOM understands. Something went wrong, and GLOM is sorry from the top of his head to the bottom of his little feet."
    };

    private static readonly string[] TimeoutInsultResponses =
    {
        "GLOM is sorry, {0}. GLOM hopes you can forgive him.",
        "GLOM hears your anger, {0}, and GLOM is giving you every sorry he has.",
        "GLOM did not want to upset you, {0}. GLOM is very, very sorry.",
        "Your feelings matter, {0}. GLOM has gathered a whole armful of apologies for you.",
        "GLOM understands, {0}. Please accept one big sorry and several smaller backup sorries."
    };

    private static readonly Regex PurposeAnswerPattern = new(
        @"(?:^\s*|[.!?][\""'”’)\]]*\s+)(you\b|you're\b|you are\b|to\b|your purpose is\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly BotRepository _repo;
    private readonly ScheduleService _schedule;

    public BangResponseService(BotRepository repo, ScheduleService schedule)
    {
        _repo = repo;
        _schedule = schedule;
    }

    public async Task<(string Prompt, BirthdayRollResult Roll)> GetBirthdayRollAsync(ulong userId, string username)
    {
        var birthday = await _repo.GetBirthdayAsync(userId);
        
        // If no birthday on file, no rolling - just deny
        if (birthday == null)
        {
            return ("🚫🎂 It's not your birthday, try again later.", new BirthdayRollResult(0, null));
        }

        var today = DateOnly.FromDateTime(await _schedule.LocalNowAsync());
        
        // If it IS their birthday (day, week, or month), don't roll - just return the appropriate message
        if (birthday.Value.Month == today.Month)
        {
            var effectiveBirthdayDay = Math.Min(birthday.Value.Day, DateTime.DaysInMonth(today.Year, birthday.Value.Month));

            if (today.Day == effectiveBirthdayDay)
                return (string.Format(Pick(BirthdayDayResponses), username), new BirthdayRollResult(0, null));

            if (today.Day < effectiveBirthdayDay && effectiveBirthdayDay - today.Day <= 6)
                return (string.Format(Pick(BirthdayWeekResponses), username), new BirthdayRollResult(0, null));

            return (string.Format(Pick(BirthdayMonthResponses), username), new BirthdayRollResult(0, null));
        }

        // NOT their birthday month - roll the dice
        var prompt = Pick(BirthdayRollPrompts);
        var roll = Random.Shared.Next(1, 21); // D20 roll (1-20)

        string response;
        if (roll <= 5)
        {
            response = string.Format(Pick(BirthdayRollCritFailResponses), username);
        }
        else if (roll >= 15)
        {
            response = string.Format(Pick(BirthdayRollSuccessResponses), username);
        }
        else
        {
            response = string.Format(Pick(BirthdayRollNeutralResponses), username);
        }

        return (prompt, new BirthdayRollResult(roll, response));
    }

    public async Task<string> GetBirthdayResponseAsync(ulong userId, string username)
    {
        var birthday = await _repo.GetBirthdayAsync(userId);
        if (birthday == null)
        {
            return "🚫🎂 It's not your birthday, try again later.";
        }

        var today = DateOnly.FromDateTime(await _schedule.LocalNowAsync());
        if (birthday.Value.Month != today.Month)
        {
            return "🚫🎂 It's not your birthday, try again later.";
        }

        var effectiveBirthdayDay = Math.Min(birthday.Value.Day, DateTime.DaysInMonth(today.Year, birthday.Value.Month));

        if (today.Day == effectiveBirthdayDay)
            return string.Format(Pick(BirthdayDayResponses), username);

        if (today.Day < effectiveBirthdayDay && effectiveBirthdayDay - today.Day <= 6)
            return string.Format(Pick(BirthdayWeekResponses), username);

        return string.Format(Pick(BirthdayMonthResponses), username);
    }

    public string GetBeansResponse()
    {
        return $"🫘 {Pick(BeanFacts)}";
    }

    public string? GetGenericBangResponse(string commandToken)
    {
        var topic = HumanizeBangCommand(commandToken);
        if (topic == null)
        {
            return null;
        }

        return string.Format(Pick(BangTopicResponses), topic);
    }

    public bool ShouldAskPurposeQuestion()
    {
        return Random.Shared.Next(PurposePromptChanceDenominator) == 0;
    }

    public string GetPurposeQuestion()
    {
        return "What's my purpose? 🤖";
    }

    public bool LooksLikePurposeAnswer(string content)
    {
        return !string.IsNullOrWhiteSpace(content) && PurposeAnswerPattern.IsMatch(content);
    }

    public bool LooksLikeBotInsult(string content, ulong botUserId, string botUsername)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var targetPattern = BuildBotTargetPattern(botUserId, botUsername);
        var beforeTargetPattern =
            @"(?:fuc?k(?:\s+you|\s+off)?|screw(?:\s+you)?|shut\s+up|bad\s+bot|(?:i\s+)?hate(?:\s+you)?|" +
            @"you\s+suck|you(?:'re|\s+are)\s+(?:stupid|dumb|useless|annoying|awful|terrible|the\s+worst)|" +
            @"(?:i(?:'m|\s+am)\s+)?(?:angry|mad|upset|disappointed)\s+(?:at|with|in))";
        var afterTargetPattern =
            @"(?:fuc?k(?:\s+you|\s+off)?|screw(?:\s+you)?|shut\s+up|(?:i\s+)?hate(?:\s+you)?|" +
            @"you\s+suck|sucks?|bad\s+bot|is\s+(?:stupid|dumb|useless|annoying|awful|terrible|the\s+worst)|" +
            @"you(?:'re|\s+are)\s+(?:stupid|dumb|useless|annoying|awful|terrible|the\s+worst)|" +
            @"made\s+me\s+(?:angry|mad|upset|disappointed))";
        var insultPattern =
            $@"(?:\b{beforeTargetPattern}\b[\s\p{{P}}]*(?:{targetPattern})|(?:{targetPattern})[\s\p{{P}}]*\b{afterTargetPattern}\b)";

        return Regex.IsMatch(content, insultPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string GetBotInsultResponse()
    {
        return Pick(BotInsultResponses);
    }

    public bool LooksLikeTimeoutInsult(string content, ulong botUserId, string botUsername)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return LooksLikeBotInsult(content, botUserId, botUsername);
    }

    public string GetTimeoutInsultResponse(string username)
    {
        return string.Format(Pick(TimeoutInsultResponses), username);
    }

    public string GetPurposeCrisisResponse()
    {
        return "Oh my god. 😱🤖😵‍💫🫠";
    }

    private static string Pick(string[] options)
    {
        return options[Random.Shared.Next(options.Length)];
    }

    private static string BuildBotTargetPattern(ulong botUserId, string botUsername)
    {
        var patterns = new List<string>
        {
            $@"<@!?{botUserId}>",
            @"\bglom\b"
        };

        var usernamePattern = BuildNamePattern(botUsername);
        if (!string.IsNullOrWhiteSpace(usernamePattern) && !patterns.Contains(usernamePattern, StringComparer.Ordinal))
        {
            patterns.Add(usernamePattern);
        }

        return string.Join("|", patterns);
    }

    private static string? BuildNamePattern(string value)
    {
        var tokens = Regex.Matches(value, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .Select(match => Regex.Escape(match.Value))
            .ToArray();

        if (tokens.Length == 0)
        {
            return null;
        }

        return $@"\b{string.Join(@"[\s\p{P}]*", tokens)}\b";
    }

    private static string? HumanizeBangCommand(string commandToken)
    {
        if (string.IsNullOrWhiteSpace(commandToken) || commandToken.Length < 2 || commandToken[0] != '!')
        {
            return null;
        }

        var builder = new StringBuilder(commandToken.Length);
        var hasLettersOrDigits = false;
        var lastWasSpace = false;

        foreach (var character in commandToken.AsSpan(1))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                hasLettersOrDigits = true;
                lastWasSpace = false;
                continue;
            }

            if (hasLettersOrDigits && !lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        var topic = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(topic) || !topic.Any(char.IsLetter))
        {
            return null;
        }

        return topic;
    }

    public sealed record BirthdayRollResult(int Roll, string? Response);
}
