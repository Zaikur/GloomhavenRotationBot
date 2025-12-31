using System.IO;
using System.Net;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GloomhavenRotationBot;
using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    // LAN-only app: avoid DP/antiforgery issues inside containers
    options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute());
});

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
builder.Services.AddSingleton<BotRepository>();
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<BotStatusService>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<AnnouncementSender>();
builder.Services.AddHostedService<MorningAnnouncementService>();
builder.Services.AddHostedService<AutoAdvanceService>();
builder.Services.AddHostedService<BotRuntimeService>();

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var ip = ctx.Connection.RemoteIpAddress;

    if (ip is null)
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("Forbidden");
        return;
    }

    // Normalize IPv4-mapped IPv6 addresses (e.g. ::ffff:192.168.1.10)
    if (ip.IsIPv4MappedToIPv6)
        ip = ip.MapToIPv4();

    bool allowed =
        IPAddress.IsLoopback(ip) ||
        IsPrivateV4(ip);

    if (!allowed)
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("Forbidden");
        return;
    }

    await next();
});

static bool IsPrivateV4(IPAddress ip)
{
    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        return false;

    var b = ip.GetAddressBytes();
    // 10.0.0.0/8
    if (b[0] == 10) return true;
    // 192.168.0.0/16
    if (b[0] == 192 && b[1] == 168) return true;
    // 172.16.0.0/12  (172.16.0.0 - 172.31.255.255)
    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;

    return false;
}


app.UseStaticFiles();
app.MapRazorPages();

// basic health
app.MapGet("/health", () => Results.Ok("ok"));

app.Run();