using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GloomhavenRotationBot;
using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Web UI
builder.Services.AddRazorPages();

// Persist DP keys so encrypted token can be decrypted after restart (important for containers)
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "data", "keys")));

// Discord services
builder.Services.AddSingleton(sp => new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers,
    AlwaysDownloadUsers = true,
    LogGatewayIntentWarnings = true
}));

builder.Services.AddSingleton(sp => new InteractionService(
    sp.GetRequiredService<DiscordSocketClient>(),
    new InteractionServiceConfig
    {
        DefaultRunMode = RunMode.Async,
        LogLevel = LogSeverity.Info
    }));

builder.Services.AddSingleton<InteractionHandler>();
builder.Services.AddSingleton<GuildMemberDirectory>();

// Data + settings
builder.Services.AddSingleton<BotRepository>();
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<BotStatusService>();

// Bot runtime
builder.Services.AddHostedService<BotRuntimeService>();

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

// basic health
app.MapGet("/health", () => Results.Ok("ok"));

app.Run();
