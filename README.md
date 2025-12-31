# GloomhavenRotationBot

A self-hosted Discord bot + local Web UI for managing a Gloomhaven group:

- Tracks **who’s up next** for **DM** and **Food**
- Lets you **skip/swap** turns without removing someone from the rotation
- Maintains a **session calendar** (month view) with per-occurrence **cancel/move/note**
- Sends a **morning announcement** to a configured channel (and supports a **Test** button)
- Can **auto-advance** rotations after a session time passes (if not cancelled)
- Designed to run as a container on **TrueNAS SCALE** (or any Docker host)

Everything is stored in a local SQLite database on a mounted volume.

---

## Features

### Discord Commands (ephemeral / private)
Commands respond privately (ephemeral), so channels don’t get spammed.

- `/gloom who dm` – shows who is up next for DM
- `/gloom who food` – shows who is up next for Food
- `/gloom skip dm "reason"` – swaps current DM with next (does not remove anyone)
- `/gloom skip food "reason"` – same for Food
- `/gloom next 4` – shows upcoming sessions
- `/gloom cancel 2026-01-05 "Out of town"` – cancels an occurrence (stores note like `User: Out of town`)
- `/gloom move 2026-01-05 2026-01-06 18:30 "Moved to Tue"` – moves an occurrence
- `/gloom clear 2026-01-05` – clears cancel/move/note override
- `/gloom preview` – shows the message that would be posted by the morning announcement (privately)

> Note: Command list may differ slightly if you renamed modules/roles. Check the Discord slash command menu.

### Web UI (LAN only)
A local web UI for setup and maintenance:

- Setup: Discord token, guild id, register-to-guild toggle
- Announcements: target channel id + time + “Test morning announcement”
- Auto-advance: minutes after start
- Schedule: weekly/monthly recurrence rule (weekday + time, interval, etc.)
- Calendar: month view with “today” highlight and an overlay editor to cancel/move/note
- Rosters: manage DM and Food member ordering (if you keep this page)

---

## Tech Stack

- .NET 8
- ASP.NET Core Razor Pages (Web UI)
- Discord.Net (Socket + Interactions)
- SQLite (local persistence)
- Docker image published to GHCR

---

## Setup (Discord)

1. Create an application in the Discord Developer Portal.
2. Add a **Bot** to the application.
3. Enable **Server Members Intent** (required if you list members in Web UI).
4. Invite the bot to your server (guild) with permissions:
   - `applications.commands`
   - `Send Messages` (for announcements)
   - `Read Message History` (optional)
   - Anything else you choose

---

## Running Locally (Visual Studio)

1. Open the solution in Visual Studio.
2. Run the project.
3. Open the Web UI:
   - `http://localhost:5055` (or whatever port you configured)
4. Go to **Setup**:
   - Enter Guild ID
   - Paste bot token
   - Set announcement channel/time (optional)
   - Save
5. The bot will connect shortly after saving.

> Token is stored in the local SQLite DB (not in user-secrets).

---

## Docker / TrueNAS SCALE (recommended)

### Container Image
Images are published to GitHub Container Registry (GHCR):

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
      - /mnt/pathTo/YourAppDirectory
```
## Accessing the Web UI

After deploying, open:

- `http://<truenas-ip>:5055`

## Updating on TrueNAS

TrueNAS **Custom Apps** do **not** auto-update when `:latest` changes. To pull new changes:

- **Edit** the app and click **Save** (redeploy), **or**
- **Stop** / **Start** the app

With `pull_policy: always`, the image will be pulled during redeploy.

## Data & Persistence

The bot stores its state in **SQLite** under the mounted volume.

### Things stored
- Discord config (encrypted token + guild id)
- Announcement config (channel id + time)
- Schedule recurrence rule
- Session overrides (cancel/move/note)
- Rotation rosters (DM/Food) and current index
- “Already announced” markers (to prevent double morning announcements)

### To migrate or reset
- Stop the container
- Backup or delete the SQLite file under the mounted volume
- Restart

## Common Troubleshooting

### “Missing Access” / command registration errors
- Ensure the bot is in the guild and has permissions.
- Ensure `GuildId` is correct.
- If registering commands to a guild, the bot must be able to access that guild.

### Announcements don’t send
- Announcement Channel ID must be a real channel in the guild.
- Bot needs permission to send messages to that channel.
- Bot must be connected (token/guild saved).
- Use **Test morning announcement** from Setup to validate.

### Web UI not reachable in Docker
- Confirm `ASPNETCORE_URLS=http://0.0.0.0:5055`
- Confirm container port is mapped: `5055:5055`
- Confirm host firewall rules

## Development Notes
- Prefer publishing versioned tags (sha/semver) for predictable deployments.
- Slash commands should be ephemeral to reduce spam.
- Schedule recurrence rule drives:
  - Calendar generation
  - Morning announcements
  - Auto-advance behavior
