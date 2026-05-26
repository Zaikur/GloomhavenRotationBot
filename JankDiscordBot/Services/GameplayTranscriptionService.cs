using System.Diagnostics;
using System.Text.Json;

namespace GloomhavenRotationBot.Services;

public sealed record TranscriptSessionState(
    string SessionId,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    int ExpectedSpeakers,
    string Status,
    string AudioFilePath,
    string? Error,
    DateTime UpdatedUtc);

public sealed record TranscriptSegment(
    double StartSeconds,
    double EndSeconds,
    string Speaker,
    string Text,
    double? Confidence);

public sealed record TranscriptSpeakerAssignment(
    string Speaker,
    ulong? PlayerId,
    string? PlayerName,
    DateTime UpdatedUtc);

public sealed class GameplayTranscriptionService
{
    public const int ChunkSeconds = 45;

    private readonly AppSettingsService _settings;
    private readonly ILogger<GameplayTranscriptionService> _log;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string? _activeRoot;
    private string? _activeSessionDir;
    private string? _activeSessionId;
    private int _nextChunkIndex;
    private int _activeExpectedSpeakers;
    private readonly List<Task> _processingTasks = new();

    public GameplayTranscriptionService(AppSettingsService settings, ILogger<GameplayTranscriptionService> log)
    {
        _settings = settings;
        _log = log;
    }

    public static string DefaultCommandTemplate =>
        "whisperx {input} --model medium --language en --diarize --min_speakers {speakers} --max_speakers {speakers} --output_dir {output}";

    private static readonly string? BundledWhisperxPath = ResolveBundledWhisperxPath();

    // ─── Model cache state ───────────────────────────────────────────────────────
    public enum ModelCacheState { Unknown, Downloading, Ready, Failed }

    private volatile ModelCacheState _modelCacheState = ModelCacheState.Unknown;
    private string? _modelDownloadError;

    public ModelCacheState GetModelCacheState() => _modelCacheState;
    public string? GetModelDownloadError() => _modelDownloadError;

    /// <summary>Checks the on-disk cache to determine if the configured Whisper model has been downloaded.</summary>
    public async Task<ModelCacheState> RefreshModelCacheStateAsync(CancellationToken ct = default)
    {
        if (_modelCacheState is ModelCacheState.Downloading)
            return _modelCacheState;

        if (OperatingSystem.IsWindows())
        {
            _modelCacheState = ModelCacheState.Unknown;
            return _modelCacheState;
        }

        var (commandTemplate, _) = await _settings.GetTranscriptionConfigAsync();
        var modelName = ParseModelName(commandTemplate);
        var hubDir = "/app/data/ml-cache/huggingface/hub";
        var whisperDir = Path.Combine(hubDir, $"models--Systran--faster-whisper-{modelName}");
        _modelCacheState = Directory.Exists(whisperDir) ? ModelCacheState.Ready : ModelCacheState.Unknown;
        return _modelCacheState;
    }

    /// <summary>Starts a background task that downloads the configured Whisper (and optionally diarization) models.</summary>
    public async Task StartModelDownloadAsync()
    {
        if (_modelCacheState is ModelCacheState.Downloading)
            return;

        _modelCacheState = ModelCacheState.Downloading;
        _modelDownloadError = null;

        var (commandTemplate, _) = await _settings.GetTranscriptionConfigAsync();
        var modelName = ParseModelName(commandTemplate);
        var hfToken = await _settings.GetHuggingFaceTokenAsync();

        _ = Task.Run(async () =>
        {
            try
            {
                var python = File.Exists("/opt/whisperx-venv/bin/python3")
                    ? "/opt/whisperx-venv/bin/python3"
                    : "python3";

                // Write a helper script to /tmp to avoid shell quoting issues with inline -c code.
                const string scriptPath = "/tmp/glom_dl_models.py";
                await File.WriteAllTextAsync(scriptPath, $"""
import os, sys
print("Downloading Whisper model '{modelName}'...", flush=True)
from faster_whisper import WhisperModel
WhisperModel("{modelName}")
print("Whisper model ready.", flush=True)
token = os.environ.get("HUGGINGFACE_TOKEN", "")
if token:
    print("Downloading diarization model...", flush=True)
    from pyannote.audio import Pipeline
    Pipeline.from_pretrained("pyannote/speaker-diarization-3.1", use_auth_token=token)
    print("Diarization model ready.", flush=True)
else:
    print("No HUGGINGFACE_TOKEN set, skipping diarization model.", flush=True)
print("Done.", flush=True)
""");

                var (exitCode, stdout, stderr) = await RunShellCommandAsync(
                    $"{python} {scriptPath}", hfToken, CancellationToken.None);

                _log.LogInformation("Model download stdout: {Stdout}", stdout);
                if (!string.IsNullOrWhiteSpace(stderr))
                    _log.LogInformation("Model download stderr: {Stderr}", stderr);

                if (exitCode != 0)
                {
                    _modelDownloadError = FirstNonEmptyLine(stderr) ?? FirstNonEmptyLine(stdout) ?? "Model download failed.";
                    _modelCacheState = ModelCacheState.Failed;
                    _log.LogError("Model download failed (exit {Code}): {Error}", exitCode, _modelDownloadError);
                    return;
                }

                _modelCacheState = ModelCacheState.Ready;
                _log.LogInformation("Model download completed successfully.");
            }
            catch (Exception ex)
            {
                _modelDownloadError = ex.Message;
                _modelCacheState = ModelCacheState.Failed;
                _log.LogError(ex, "Model download threw an exception.");
            }
        }, CancellationToken.None);
    }

    private static string ParseModelName(string commandTemplate)
    {
        var m = System.Text.RegularExpressions.Regex.Match(commandTemplate, @"--model\s+(\S+)");
        return m.Success ? m.Groups[1].Value : "medium";
    }

    public async Task<(bool Ok, string Message)> StartSessionAsync(int expectedSpeakers, CancellationToken ct = default)
    {
        expectedSpeakers = Math.Clamp(expectedSpeakers, 1, 12);

        var (_, rootPath) = await _settings.GetTranscriptionConfigAsync();
        var root = ResolveAbsolutePath(rootPath);
        Directory.CreateDirectory(root);

        string sessionId;
        string sessionDir;

        lock (_sync)
        {
            if (_activeSessionId != null)
                return (false, "A transcription session is already active.");

            sessionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
            sessionDir = Path.Combine(root, sessionId);

            _activeSessionId = sessionId;
            _activeRoot = root;
            _activeSessionDir = sessionDir;
            _activeExpectedSpeakers = expectedSpeakers;
            _nextChunkIndex = 0;
            _processingTasks.Clear();
        }

        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "chunks"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "output"));

        await File.WriteAllTextAsync(Path.Combine(sessionDir, "segments.json"), "[]", ct);
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "speaker-aliases.json"), "{}", ct);

        var session = new TranscriptSessionState(
            SessionId: sessionId,
            StartedUtc: DateTime.UtcNow,
            EndedUtc: null,
            ExpectedSpeakers: expectedSpeakers,
            Status: "RecordingRemote",
            AudioFilePath: Path.Combine(sessionDir, "chunks"),
            Error: null,
            UpdatedUtc: DateTime.UtcNow);

        await SaveSessionAsync(session, root, ct);

        return (true, "Session started. Click Start Laptop Mic in this page to stream audio chunks.");
    }

    public async Task<(bool Ok, string Message)> StopSessionAsync(CancellationToken ct = default)
    {
        string? sessionId;
        string? root;

        lock (_sync)
        {
            sessionId = _activeSessionId;
            root = _activeRoot;

            if (sessionId == null)
                return (false, "No active recording session.");

            _activeSessionId = null;
            _activeRoot = null;
            _activeSessionDir = null;
        }

        Task[] pending;
        lock (_sync)
        {
            pending = _processingTasks.ToArray();
        }

        if (pending.Length > 0)
            await Task.WhenAll(pending);

        var (session, resolvedRoot) = await TryGetSessionAsync(sessionId, ct);
        if (session == null)
            return (false, "Session metadata could not be found.");

        var finalStatus = string.IsNullOrWhiteSpace(session.Error) ? "Completed" : "CompletedWithWarnings";
        var completed = session with
        {
            EndedUtc = DateTime.UtcNow,
            Status = finalStatus,
            UpdatedUtc = DateTime.UtcNow
        };

        await SaveSessionAsync(completed, resolvedRoot ?? root!, ct);
        return (true, "Recording stopped. Final chunk processing completed.");
    }

    public async Task<(bool Ok, string Message)> UploadChunkAsync(string sessionId, Stream chunkStream, string? originalFileName, CancellationToken ct = default)
    {
        string? activeId;
        string? sessionDir;
        int expectedSpeakers;
        int chunkIndex;

        lock (_sync)
        {
            activeId = _activeSessionId;
            sessionDir = _activeSessionDir;
            expectedSpeakers = _activeExpectedSpeakers;

            if (activeId == null || sessionDir == null)
                return (false, "No active session for uploads.");

            if (!string.Equals(activeId, sessionId, StringComparison.Ordinal))
                return (false, "Upload session does not match the active session.");

            chunkIndex = _nextChunkIndex++;
        }

        var extension = ResolveChunkExtension(originalFileName);
        var chunkPath = Path.Combine(sessionDir, "chunks", $"chunk-{chunkIndex:D5}{extension}");

        await using (var fs = File.Create(chunkPath))
        {
            await chunkStream.CopyToAsync(fs, ct);
        }

        var fi = new FileInfo(chunkPath);
        if (!fi.Exists || fi.Length == 0)
            return (false, "Uploaded audio chunk was empty.");

        var processTask = ProcessChunkAsync(sessionId, sessionDir, chunkPath, expectedSpeakers, CancellationToken.None);
        TrackProcessingTask(processTask);

        return (true, $"Accepted chunk {chunkIndex:D5}.");
    }

    public async Task<TranscriptSessionState?> GetActiveSessionAsync(CancellationToken ct = default)
    {
        string? activeId;
        lock (_sync) activeId = _activeSessionId;

        if (activeId == null)
            return null;

        var (session, _) = await TryGetSessionAsync(activeId, ct);
        return session;
    }

    public async Task<List<TranscriptSessionState>> GetRecentSessionsAsync(int maxCount = 20, CancellationToken ct = default)
    {
        var (_, rootPath) = await _settings.GetTranscriptionConfigAsync();
        var root = ResolveAbsolutePath(rootPath);

        if (!Directory.Exists(root))
            return new();

        var sessions = new List<TranscriptSessionState>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(dir, "session.json");
            if (!File.Exists(path))
                continue;

            var json = await File.ReadAllTextAsync(path, ct);
            var session = JsonSerializer.Deserialize<TranscriptSessionState>(json, _jsonOpts);
            if (session != null)
                sessions.Add(session);
        }

        return sessions
            .OrderByDescending(s => s.StartedUtc)
            .Take(Math.Max(1, maxCount))
            .ToList();
    }

    public async Task<TranscriptSessionState?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var (session, _) = await TryGetSessionAsync(sessionId, ct);
        return session;
    }

    public async Task<List<TranscriptSegment>> GetSegmentsAsync(string sessionId, CancellationToken ct = default)
    {
        var (_, root) = await TryGetSessionAsync(sessionId, ct);
        if (root == null)
            return new();

        var segmentsPath = Path.Combine(root, sessionId, "segments.json");
        if (!File.Exists(segmentsPath))
            return new();

        var json = await File.ReadAllTextAsync(segmentsPath, ct);
        return JsonSerializer.Deserialize<List<TranscriptSegment>>(json, _jsonOpts) ?? new();
    }

    public async Task<Dictionary<string, TranscriptSpeakerAssignment>> GetSpeakerAssignmentsAsync(string sessionId, CancellationToken ct = default)
    {
        var (_, root) = await TryGetSessionAsync(sessionId, ct);
        if (root == null)
            return new(StringComparer.OrdinalIgnoreCase);

        var mapPath = Path.Combine(root, sessionId, "speaker-map.json");
        if (!File.Exists(mapPath))
            return new(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(mapPath, ct);
        var map = JsonSerializer.Deserialize<Dictionary<string, TranscriptSpeakerAssignment>>(json, _jsonOpts);
        return map ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveSpeakerAssignmentAsync(string sessionId, string speaker, ulong? playerId, string? playerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            return;

        var (_, root) = await TryGetSessionAsync(sessionId, ct);
        if (root == null)
            return;

        var map = await GetSpeakerAssignmentsAsync(sessionId, ct);

        if (playerId == null && string.IsNullOrWhiteSpace(playerName))
        {
            map.Remove(speaker);
        }
        else
        {
            map[speaker] = new TranscriptSpeakerAssignment(
                Speaker: speaker,
                PlayerId: playerId,
                PlayerName: string.IsNullOrWhiteSpace(playerName) ? null : playerName,
                UpdatedUtc: DateTime.UtcNow);
        }

        var mapPath = Path.Combine(root, sessionId, "speaker-map.json");
        var json = JsonSerializer.Serialize(map, _jsonOpts);
        await File.WriteAllTextAsync(mapPath, json, ct);
    }

    private void TrackProcessingTask(Task task)
    {
        lock (_sync)
        {
            _processingTasks.Add(task);
        }

        _ = task.ContinueWith(_ =>
        {
            lock (_sync)
            {
                _processingTasks.Remove(task);
            }
        }, TaskScheduler.Default);
    }

    private async Task ProcessChunkAsync(string sessionId, string sessionDir, string chunkPath, int expectedSpeakers, CancellationToken ct)
    {
        await _processLock.WaitAsync(ct);
        try
        {
            var (commandTemplate, _) = await _settings.GetTranscriptionConfigAsync();
            if (string.IsNullOrWhiteSpace(commandTemplate))
            {
                await SetSessionErrorAsync(sessionId, "Transcription command template is empty. Save it in Transcript settings.", ct);
                _log.LogWarning("Skipping transcription for {SessionId} because command template is empty.", sessionId);
                return;
            }

            var chunkIndex = ExtractChunkIndex(chunkPath);
            var chunkBase = Path.GetFileNameWithoutExtension(chunkPath);
            var outputDir = Path.Combine(sessionDir, "output", chunkBase);
            Directory.CreateDirectory(outputDir);

            var command = BuildCommand(commandTemplate, chunkPath, outputDir, expectedSpeakers, sessionId);
            var hfToken = await _settings.GetHuggingFaceTokenAsync();
            var (exitCode, stdout, stderr) = await RunShellCommandAsync(command, hfToken, ct);

            await AppendRunLogAsync(sessionDir, chunkBase, command, exitCode, stdout, stderr, ct);

            if (exitCode == 127 && !OperatingSystem.IsWindows())
            {
                var retryCommand = ReplaceLeadingPythonWithPython3(command);
                if (!string.Equals(retryCommand, command, StringComparison.Ordinal))
                {
                    var retryResult = await RunShellCommandAsync(retryCommand, hfToken, ct);
                    exitCode = retryResult.ExitCode;
                    stdout = retryResult.StdOut;
                    stderr = retryResult.StdErr;

                    await AppendRunLogAsync(sessionDir, $"{chunkBase}-retry", retryCommand, exitCode, stdout, stderr, ct);
                }
            }

            if (exitCode != 0)
            {
                var detail = FirstNonEmptyLine(stderr) ?? FirstNonEmptyLine(stdout);
                var message = string.IsNullOrWhiteSpace(detail)
                    ? $"Chunk {chunkBase} failed with exit {exitCode}."
                    : $"Chunk {chunkBase} failed with exit {exitCode}: {detail}";

                await SetSessionErrorAsync(sessionId, message, ct);
                return;
            }

            var newSegments = await ParseChunkSegmentsAsync(sessionDir, outputDir, chunkIndex, ct);
            if (newSegments.Count == 0)
                return;

            await AppendSegmentsAsync(sessionDir, newSegments, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to process chunk {ChunkPath} for {SessionId}", chunkPath, sessionId);
            await SetSessionErrorAsync(sessionId, ex.Message, CancellationToken.None);
        }
        finally
        {
            _processLock.Release();
        }
    }

    private async Task<List<TranscriptSegment>> ParseChunkSegmentsAsync(
        string sessionDir,
        string outputDir,
        int chunkIndex,
        CancellationToken ct)
    {
        var jsonPath = Directory.GetFiles(outputDir, "*.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (jsonPath == null)
            return new();

        var raw = await File.ReadAllTextAsync(jsonPath, ct);
        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("segments", out var segmentsEl) || segmentsEl.ValueKind != JsonValueKind.Array)
            return new();

        var aliases = await ReadSpeakerAliasesAsync(sessionDir, ct);
        var normalized = new List<TranscriptSegment>();
        var chunkOffset = chunkIndex * ChunkSeconds;

        foreach (var seg in segmentsEl.EnumerateArray())
        {
            var start = TryGetDouble(seg, "start");
            var end = TryGetDouble(seg, "end");
            var text = TryGetString(seg, "text")?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var rawSpeaker = TryGetString(seg, "speaker");
            if (string.IsNullOrWhiteSpace(rawSpeaker))
                rawSpeaker = "UNKNOWN";

            if (!aliases.TryGetValue(rawSpeaker, out var speakerLabel))
            {
                speakerLabel = $"Speaker {aliases.Count + 1}";
                aliases[rawSpeaker] = speakerLabel;
            }

            var confidence = TryGetDoubleNullable(seg, "confidence")
                ?? TryGetDoubleNullable(seg, "avg_logprob");

            normalized.Add(new TranscriptSegment(chunkOffset + start, chunkOffset + end, speakerLabel, text, confidence));
        }

        await WriteSpeakerAliasesAsync(sessionDir, aliases, ct);
        return normalized;
    }

    private async Task<Dictionary<string, string>> ReadSpeakerAliasesAsync(string sessionDir, CancellationToken ct)
    {
        var path = Path.Combine(sessionDir, "speaker-aliases.json");
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(path, ct);
        var aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOpts);
        return aliases ?? new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteSpeakerAliasesAsync(string sessionDir, Dictionary<string, string> aliases, CancellationToken ct)
    {
        var path = Path.Combine(sessionDir, "speaker-aliases.json");
        var json = JsonSerializer.Serialize(aliases, _jsonOpts);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task AppendSegmentsAsync(string sessionDir, List<TranscriptSegment> segmentsToAppend, CancellationToken ct)
    {
        var path = Path.Combine(sessionDir, "segments.json");
        var existing = new List<TranscriptSegment>();

        if (File.Exists(path))
        {
            var raw = await File.ReadAllTextAsync(path, ct);
            existing = JsonSerializer.Deserialize<List<TranscriptSegment>>(raw, _jsonOpts) ?? new();
        }

        existing.AddRange(segmentsToAppend);
        existing = existing
            .OrderBy(s => s.StartSeconds)
            .ThenBy(s => s.EndSeconds)
            .ToList();

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(existing, _jsonOpts), ct);
    }

    private async Task AppendRunLogAsync(string sessionDir, string chunkName, string command, int exitCode, string stdout, string stderr, CancellationToken ct)
    {
        var path = Path.Combine(sessionDir, "run.log");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] Chunk {chunkName}");
        sb.AppendLine($"Command: {command}");
        sb.AppendLine($"ExitCode: {exitCode}");
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sb.AppendLine("STDOUT:");
            sb.AppendLine(stdout);
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine("STDERR:");
            sb.AppendLine(stderr);
        }
        sb.AppendLine();

        await File.AppendAllTextAsync(path, sb.ToString(), ct);
    }

    private async Task SetSessionErrorAsync(string sessionId, string error, CancellationToken ct)
    {
        var (session, root) = await TryGetSessionAsync(sessionId, ct);
        if (session == null || root == null)
            return;

        var updated = session with
        {
            Error = error,
            UpdatedUtc = DateTime.UtcNow
        };

        await SaveSessionAsync(updated, root, ct);
    }

    private async Task SaveSessionAsync(TranscriptSessionState session, string root, CancellationToken ct)
    {
        var dir = Path.Combine(root, session.SessionId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.json");
        var json = JsonSerializer.Serialize(session, _jsonOpts);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task<(TranscriptSessionState? Session, string? Root)> TryGetSessionAsync(string sessionId, CancellationToken ct)
    {
        var (_, rootPath) = await _settings.GetTranscriptionConfigAsync();
        var root = ResolveAbsolutePath(rootPath);

        var path = Path.Combine(root, sessionId, "session.json");
        if (!File.Exists(path))
            return (null, root);

        var json = await File.ReadAllTextAsync(path, ct);
        var session = JsonSerializer.Deserialize<TranscriptSessionState>(json, _jsonOpts);
        return (session, root);
    }

    private static string BuildCommand(string template, string inputPath, string outputDir, int speakers, string sessionId)
    {
        var command = template
            .Replace("{input}", QuoteForShell(inputPath), StringComparison.Ordinal)
            .Replace("{output}", QuoteForShell(outputDir), StringComparison.Ordinal)
            .Replace("{speakers}", speakers.ToString(), StringComparison.Ordinal)
            .Replace("{sessionId}", QuoteForShell(sessionId), StringComparison.Ordinal);

        return NormalizeWhisperxCommand(command);
    }

    private static string NormalizeWhisperxCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(BundledWhisperxPath))
            return command;

        var trimmed = command.TrimStart();
        var leadingWhitespaceLength = command.Length - trimmed.Length;
        var leadingWhitespace = leadingWhitespaceLength > 0 ? command[..leadingWhitespaceLength] : string.Empty;

        if (trimmed.StartsWith("whisperx ", StringComparison.OrdinalIgnoreCase))
        {
            return $"{leadingWhitespace}{BundledWhisperxPath} {trimmed["whisperx ".Length..]}";
        }

        if (trimmed.Equals("whisperx", StringComparison.OrdinalIgnoreCase))
        {
            return $"{leadingWhitespace}{BundledWhisperxPath}";
        }

        return command;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunShellCommandAsync(string command, string? hfToken, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var shell = isWindows ? "cmd.exe" : "/bin/bash";
        var shellArgs = isWindows ? $"/C {command}" : $"-lc {QuoteForShell(command)}";

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = shellArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(hfToken))
            psi.Environment["HUGGINGFACE_TOKEN"] = hfToken;

        if (!OperatingSystem.IsWindows())
        {
            // Redirect all Python/ML tool cache and config dirs to the app data directory so they:
            //   1) survive container restarts (models don't need to re-download), and
            //   2) are writable by non-root users like 568:568 without touching /.config or ~/.cache.
            const string mlCache = "/app/data/ml-cache";
            psi.Environment["MPLCONFIGDIR"] = mlCache + "/matplotlib";
            psi.Environment["XDG_CONFIG_HOME"] = mlCache + "/xdg-config";
            psi.Environment["NUMBA_CACHE_DIR"] = mlCache + "/numba";
            psi.Environment["HF_HOME"] = mlCache + "/huggingface";
            psi.Environment["TRANSFORMERS_CACHE"] = mlCache + "/huggingface/hub";
            psi.Environment["TORCH_HOME"] = mlCache + "/torch";
            psi.Environment["HOME"] = "/tmp";
        }

        if (!string.IsNullOrWhiteSpace(BundledWhisperxPath))
        {
            var binDir = Path.GetDirectoryName(BundledWhisperxPath);
            if (!string.IsNullOrWhiteSpace(binDir))
            {
                var existingPath = psi.Environment.TryGetValue("PATH", out var currentPath) ? currentPath : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                psi.Environment["PATH"] = string.IsNullOrWhiteSpace(existingPath)
                    ? binDir
                    : $"{binDir}:{existingPath}";
            }
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start shell process.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (process.ExitCode, stdOut, stdErr);
    }

    private static int ExtractChunkIndex(string chunkPath)
    {
        var name = Path.GetFileNameWithoutExtension(chunkPath);
        var dash = name.LastIndexOf("-", StringComparison.Ordinal);
        if (dash < 0)
            return 0;

        return int.TryParse(name[(dash + 1)..], out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static string ResolveAbsolutePath(string path)
        => Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    private static string QuoteForShell(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string? ResolveBundledWhisperxPath()
    {
        if (OperatingSystem.IsWindows())
            return null;

        var bundledPath = "/opt/whisperx-venv/bin/whisperx";
        return File.Exists(bundledPath) ? bundledPath : null;
    }

    private static string ReplaceLeadingPythonWithPython3(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return command;

        var trimmed = command.TrimStart();
        if (!trimmed.StartsWith("python ", StringComparison.OrdinalIgnoreCase))
            return command;

        var leadingWhitespaceLength = command.Length - trimmed.Length;
        var leadingWhitespace = leadingWhitespaceLength > 0 ? command[..leadingWhitespaceLength] : string.Empty;
        var tail = trimmed["python ".Length..];
        return $"{leadingWhitespace}python3 {tail}";
    }

    private static string ResolveChunkExtension(string? originalFileName)
    {
        var ext = Path.GetExtension(originalFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(ext))
            return ".webm";

        ext = ext.Trim().ToLowerInvariant();
        return ext is ".wav" or ".webm" or ".ogg" or ".mp3" or ".m4a" or ".mp4"
            ? ext
            : ".webm";
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double TryGetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;

        return value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static double? TryGetDoubleNullable(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();

        return double.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static string? FirstNonEmptyLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        foreach (var line in value.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }

        return null;
    }
}
