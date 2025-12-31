namespace GloomhavenRotationBot.Services;

/// <summary>
/// Represents one scheduled occurrence (keyed by OriginalDateLocal), including any override (cancel/move/note).
/// </summary>
public sealed record SessionInfo(
    string OccurrenceId,            // e.g. "default:2025-12-30"
    DateOnly OriginalDateLocal,     // the "base" occurrence date (before any move)
    DateTime EffectiveStartLocal,   // actual local start time (after move override)
    bool IsCancelled,
    string? Note
);