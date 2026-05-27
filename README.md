# GloomhavenRotationBot

Let's call this an experiment in vibe coding. A discord bot you can self host to keep track of your Gloomhaven members, who's turn it is to DM, who is responsible for food, birthdays for some reason, a few slash commands, and random !bang commands my party members keep requesting. One day maybe I'll get around to integrating a tiny LLM to respond appropriately.

This app was vibe coded as fast as possible with the absolute minimum of checks (vibe coded might be too strong of a term, zombie coded?), and refined to the point that the secrets aren't stored in the repository, and the UI looks ok if you squint really hard <sub><sup><sup>don't even think about mobile</sup></sup></sub>. Beyond that, I promise nothing.

Right here is where human contribution to this project ends.




A self-hosted Discord bot + local Web UI for managing a Gloomhaven group:

- Tracks **who’s up next** for **DM** and **Food**
- Lets you **skip/swap** turns without removing someone from the rotation
- Maintains a **session calendar** (month view) with per-occurrence **cancel/move/note**
- Sends a **morning announcement** to a configured channel
- Can **auto-advance** rotations after a session time passes if the session wasn't cancelled.
- Includes a **Broadcast** UI page to send custom Discord-formatted messages as the bot
- **Chatbot** responds to questions like "are we doing this tonight?" with next session details
- Designed to run as a container on **TrueNAS SCALE** (or any Docker host)

Everything is stored in a local SQLite database on a mounted volume.

---

## Features

### Rotation, schedule, and automation

- Tracks who is up next for DM and Food
- Lets you reorder rosters and set the current DM/Food member from the web UI
- Supports a weekly or monthly recurring session rule with timezone, interval, weekday, time, and monthly-week settings
- Maintains a session calendar with per-occurrence cancel, move, and note overrides
- Sends a morning announcement to a configured Discord channel on session days
- Auto-advances DM and Food after the session start time plus a configurable grace period
- Stores birthdays and sends birthday announcements automatically once per year
- Exposes a status page and a simple `/health` endpoint

### Slash commands

Slash commands respond ephemerally so channels do not get spammed.

- `/who dm`
- `/who food`
- `/advance dm`
- `/advance food`
- `/advance all`
- `/chatbot pause [minutes]`
- `/chatbot resume`
- `/chatbot status`

### Mention chatbot

Mention the bot in a message to get natural-language answers about:

- whether the next session is happening
- when the next session is
- who is DM
- whether you are DM
- who is bringing food
- whether you are bringing food
- whether the next session is cancelled and why
- simple greetings and thank-yous

The bot only responds in the configured guild. Bang commands are the exception to the "mention me" rule, but they still only work in that configured guild.

### Bang commands and other nonsense

Bang commands do not require a bot mention.

Special cases:

- `!nextsession` returns the same next-session summary as the chatbot
- `!itsmybirthday` checks the birthday stored for your Discord user and responds differently for birthday day, birthday week, birthday month, or "no"
- `!beans` returns a random bean fact

Generic bang behavior:

- most other `!whatever` commands get humanized into a topic and answered with a short snarky line
- commands with no useful letters are ignored
- after a successful bang response, the bot may ask a one-off `What's my purpose?` follow-up
- if the same user answers that prompt in the same channel within 10 minutes, the bot replies with an existential crisis message
- the purpose prompt is only meant to happen once per user unless you reset that history in Setup
- directly insulting the bot can also get a response

### Birthdays

- Birthdays are managed from the Rosters page and stored by Discord user ID
- `!itsmybirthday` uses the stored date and the app's local timezone
- A background service checks birthdays daily at 9:00 local time
- Birthday announcements go to the same announcement channel used for morning session posts
- Automated birthday posts are deduplicated with a per-user last-sent-year value

### Broadcast

- Broadcast page sends a one-off message to the configured announcement channel exactly as entered
- Supports Discord markdown, emoji syntax, mentions, and line breaks

### Web UI

The LAN-only admin UI includes:

- Status: Discord runtime state and connection details
- Setup: guild ID, bot token, announcement channel/time, auto-advance delay, token test, morning announcement test, and bang prompt reset
- Rosters: DM/Food ordering, current member selection, and birthday storage
- Schedule: timezone-aware weekly/monthly recurrence configuration
- Calendar: month view with cancel, move, and note overrides for individual occurrences
- Broadcast: send a manual Discord message as the bot

## Tech Stack

- .NET 8
- ASP.NET Core Razor Pages
- Discord.Net (Socket + Interactions)
- SQLite
- Docker image published to GHCR

## Discord Setup

1. Create an application in the Discord Developer Portal.
2. Add a bot user to the application.
3. Enable these privileged gateway intents:
   - Server Members Intent
   - Message Content Intent
4. Invite the bot to your server with these scopes:
   - `applications.commands`
   - `bot`
5. Give the bot at least these permissions:
   - `Send Messages`
   - `View Channels`
   - `Read Message History`

Notes:

- Server Members Intent is required for the Rosters page and birthday management.
- Message Content Intent is required for mention-based chatbot replies and `!bang` commands.
- Slash commands currently register globally in the running app, so Discord may take a few minutes to show new or updated commands.

## Running Locally

1. Build the solution:

```bash
dotnet build GloomhavenRotationBot.sln
```

2. Run the app:

```bash
dotnet run --project JankDiscordBot
```

3. Open the web UI at `http://localhost:5055`.
4. Go to Setup and save your Guild ID and bot token.
5. Configure Schedule, Rosters, and optional announcements.

The bot monitors the saved SQLite settings and reconnects automatically after you save changes.

## Docker / TrueNAS SCALE

### Container Image

- `ghcr.io/zaikur/gloomhavenrotationbot:latest`

### Example compose / TrueNAS Custom App YAML

```yaml
services:
  gloom-bot:
    image: ghcr.io/zaikur/gloomhavenrotationbot:latest
    pull_policy: always
    container_name: gloom-bot
    environment:
      ASPNETCORE_URLS: http://0.0.0.0:5055
      DOTNET_ENVIRONMENT: Production
    ports:
      - "5055:5055"
    restart: unless-stopped
    volumes:
      - /mnt/pathTo/YourAppDirectory:/app/data
```

The default database path is `data/app.db`, so mounting `/app/data` preserves your SQLite file across container restarts.

### Accessing the Web UI

After deploying, open one of these from the same machine or your private network:

- `http://localhost:5055`
- `http://<private-lan-ip>:5055`

The app rejects public or non-private source IPs by design.

### Updating on TrueNAS

TrueNAS Custom Apps do not auto-update when `:latest` changes. To pull new changes:

- Edit the app and click Save to redeploy it

With `pull_policy: always`, the image will be pulled during redeploy.

## Data and Persistence

The bot stores its core app state in SQLite.

### Things stored

- Discord settings: bot token, guild ID, and command-registration preference
- Announcement settings: channel ID and time
- Auto-advance setting: minutes after session start
- Schedule rule: timezone, frequency, interval, day of week, time, monthly week, and anchor date
- Rotation rosters: DM/Food member order and current index
- Session overrides: cancelled flag, moved date/time, and per-session note
- Session markers: whether an occurrence was already announced or auto-advanced, plus timestamps
- Birthdays: month, day, and the last year an automated birthday message was sent
- Bang-command state: the list of users who have already seen the one-off purpose prompt

### To migrate or reset

- Stop the app or container
- Back up or delete the SQLite database file
- Start the app or container again

In the default container layout, the database lives under `/app/data/app.db`.

## Common Troubleshooting

### Slash commands are missing

- Make sure the bot was invited with the `applications.commands` scope.
- Give Discord a little time. Commands currently register globally, not guild-only.

### Mention chatbot or bang commands do not respond

- Make sure Message Content Intent is enabled.
- Make sure the message is being sent in the configured guild.
- Mention the bot for natural-language questions. Only `!bang` commands work without a mention.
- Confirm the bot can read the target channel.

### Rosters page shows no guild members

- Make sure Server Members Intent is enabled.
- Make sure the bot is connected and the Guild ID is correct.

### Announcements or birthday posts do not send

- Set a real announcement channel ID in Setup.
- Make sure the bot can send messages to that channel.
- Birthday posts use the same announcement channel as morning session announcements.
- Use Test morning announcement from Setup to validate the channel.

### Web UI is not reachable in Docker

- Confirm `ASPNETCORE_URLS=http://0.0.0.0:5055`
- Confirm the port mapping is `5055:5055`
- Confirm you are connecting from localhost or a private LAN IP
- Confirm host firewall rules

