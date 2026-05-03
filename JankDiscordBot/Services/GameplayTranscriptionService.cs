using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;

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

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _currentChunkPath;
    private string? _activeRoot;
    private string? _activeSessionDir;
    private string? _activeSessionId;
    private int _nextChunkIndex;
    private int _activeExpectedSpeakers;
    private bool _stopRequested;
    private TaskCompletionSource<bool>? _recordingStopped;
    private CancellationTokenSource? _chunkLoopCts;
    private Task? _chunkLoopTask;
    private readonly List<Task> _processingTasks = new();

    public GameplayTranscriptionService(AppSettingsService settings, ILogger<GameplayTranscriptionService> log)
    {
        _settings = settings;
        _log = log;
    }

    public static string DefaultCommandTemplate =>
        "python -m whisperx {input} --model medium --language en --diarize --min_speakers {speakers} --max_speakers {speakers} --output_dir {output}";

    public async Task<(bool Ok, string Message)> StartSessionAsync(int expectedSpeakers, CancellationToken ct = default)
    {
        expectedSpeakers = Math.Clamp(expectedSpeakers, 1, 12);

        var (commandTemplate, rootPath) = await _settings.GetTranscriptionConfigAsync();
        var root = ResolveAbsolutePath(rootPath);
        Directory.CreateDirectory(root);

        string sessionId;
        string sessionDir;
        lock (_sync)
        {
            if (_activeSessionId != null)
                return (false, "A transcription session is already recording.");

            sessionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
            sessionDir = Path.Combine(root, sessionId);

            _activeSessionId = sessionId;
            _activeRoot = root;
            _activeSessionDir = sessionDir;
            _activeExpectedSpeakers = expectedSpeakers;
            _nextChunkIndex = 0;
            _stopRequested = false;
            _recordingStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _chunkLoopCts = new CancellationTokenSource();
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
            Status: string.IsNullOrWhiteSpace(commandTemplate) ? "Recording" : "RecordingLive",
            AudioFilePath: Path.Combine(sessionDir, "chunks"),
            Error: null,
            UpdatedUtc: DateTime.UtcNow);

        await SaveSessionAsync(session, root, ct);

        try
        {
            var waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 200
            };

            var firstChunk = BuildChunkPath(sessionDir, 0);
            var writer = new WaveFileWriter(firstChunk, waveIn.WaveFormat);

            waveIn.DataAvailable += (_, args) =>
            {
                lock (_sync)
                {
                    _writer?.Write(args.Buffer, 0, args.BytesRecorded);
                    _writer?.Flush();
                }
            };

            waveIn.RecordingStopped += (_, args) =>
            {
                lock (_sync)
                {
                    _waveIn?.Dispose();
                    _waveIn = null;

                    _recordingStopped?.TrySetResult(true);
                }

                if (args.Exception != null)
                    _log.LogError(args.Exception, "Microphone recording stopped with an error.");
            };

            lock (_sync)
            {
                _waveIn = waveIn;
                _writer = writer;
                _currentChunkPath = firstChunk;
            }

            waveIn.StartRecording();

            _chunkLoopTask = Task.Run(() => ChunkLoopAsync(sessionId, _chunkLoopCts!.Token), CancellationToken.None);

            if (string.IsNullOrWhiteSpace(commandTemplate))
                return (true, "Recording started. Configure a transcription command to enable live chunk processing.");

            return (true, "Recording started with live chunk processing.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start microphone recording.");

            lock (_sync)
            {
                _activeSessionId = null;
                _activeRoot = null;
                _activeSessionDir = null;
                _currentChunkPath = null;
                _chunkLoopCts?.Cancel();
                _recordingStopped?.TrySetResult(true);
            }

            var failed = session with
            {
                EndedUtc = DateTime.UtcNow,
                Status = "Failed",
                Error = $"Microphone could not start: {ex.Message}",
                UpdatedUtc = DateTime.UtcNow
            };

            await SaveSessionAsync(failed, root, ct);
            return (false, failed.Error!);
        }
    }

    public async Task<(bool Ok, string Message)> StopSessionAsync(CancellationToken ct = default)
    {
        string? sessionId;
        TaskCompletionSource<bool>? stoppedSignal;
        Task? chunkLoopTask;
        CancellationTokenSource? chunkLoopCts;

        lock (_sync)
        {
            sessionId = _activeSessionId;
            stoppedSignal = _recordingStopped;
            chunkLoopTask = _chunkLoopTask;
            chunkLoopCts = _chunkLoopCts;

            if (sessionId == null)
                return (false, "No active recording session.");

            _activeSessionId = null;
            _stopRequested = true;
        }

        chunkLoopCts?.Cancel();

        try
        {
            _waveIn?.StopRecording();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error while stopping microphone recording.");
        }

        if (stoppedSignal != null)
            await stoppedSignal.Task.WaitAsync(ct);

        if (chunkLoopTask != null)
        {
            try { await chunkLoopTask; } catch (OperationCanceledException) { }
        }

        var lastChunkTask = RotateChunkAsync(startNewChunk: false, queueForProcessing: true, ct);
        if (lastChunkTask != null)
            await lastChunkTask;

        Task[] pending;
        lock (_sync)
        {
            pending = _processingTasks.ToArray();
        }

        if (pending.Length > 0)
            await Task.WhenAll(pending);

        var (session, root) = await TryGetSessionAsync(sessionId, ct);
        if (session == null)
            return (false, "Session metadata could not be found.");

        var finalStatus = string.IsNullOrWhiteSpace(session.Error) ? "Completed" : "CompletedWithWarnings";

        var completed = session with
        {
            EndedUtc = DateTime.UtcNow,
            Status = finalStatus,
            UpdatedUtc = DateTime.UtcNow
        };

        await SaveSessionAsync(completed, root!, ct);

        lock (_sync)
        {
            _activeRoot = null;
            _activeSessionDir = null;
            _currentChunkPath = null;
            _chunkLoopCts?.Dispose();
            _chunkLoopCts = null;
            _chunkLoopTask = null;
            _recordingStopped = null;
        }

        return (true, "Recording stopped. Final chunk processing completed.");
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

    private async Task ChunkLoopAsync(string sessionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ChunkSeconds), ct);

                Task? processTask = null;
                lock (_sync)
                {
                    if (_activeSessionId != sessionId || _stopRequested)
                        return;
                }

                processTask = RotateChunkAsync(startNewChunk: true, queueForProcessing: true, ct);
                if (processTask != null)
                    await processTask;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Chunk loop failed for {SessionId}", sessionId);
                await SetSessionErrorAsync(sessionId, $"Live chunking error: {ex.Message}", CancellationToken.None);
            }
        }
    }

    private Task? RotateChunkAsync(bool startNewChunk, bool queueForProcessing, CancellationToken ct)
    {
        WaveFileWriter? oldWriter = null;
        string? closedChunkPath = null;
        string? sessionId = null;
        string? sessionDir = null;
        int expectedSpeakers = 0;

        lock (_sync)
        {
            if (_activeSessionId == null || _activeSessionDir == null || _writer == null)
                return null;

            sessionId = _activeSessionId;
            sessionDir = _activeSessionDir;
            expectedSpeakers = _activeExpectedSpeakers;

            oldWriter = _writer;
            closedChunkPath = _currentChunkPath;
            _writer = null;
            _currentChunkPath = null;

            if (startNewChunk && _waveIn != null && !_stopRequested)
            {
                _nextChunkIndex++;
                var nextPath = BuildChunkPath(sessionDir, _nextChunkIndex);
                _writer = new WaveFileWriter(nextPath, _waveIn.WaveFormat);
                _currentChunkPath = nextPath;
            }
        }

        oldWriter?.Dispose();

        if (!queueForProcessing || string.IsNullOrWhiteSpace(closedChunkPath) || !File.Exists(closedChunkPath))
            return null;

        var fi = new FileInfo(closedChunkPath);
        if (fi.Length <= 44)
            return null;

        var task = ProcessChunkAsync(sessionId!, sessionDir!, closedChunkPath, expectedSpeakers, ct);
        TrackProcessingTask(task);
        return task;
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
                return;

            var chunkIndex = ExtractChunkIndex(chunkPath);
            var chunkBase = Path.GetFileNameWithoutExtension(chunkPath);
            var outputDir = Path.Combine(sessionDir, "output", chunkBase);
            Directory.CreateDirectory(outputDir);

            var command = BuildCommand(commandTemplate, chunkPath, outputDir, expectedSpeakers, sessionId);
            var (exitCode, stdout, stderr) = await RunShellCommandAsync(command, ct);

            await AppendRunLogAsync(sessionDir, chunkBase, command, exitCode, stdout, stderr, ct);

            if (exitCode != 0)
            {
                await SetSessionErrorAsync(sessionId, $"Chunk {chunkBase} failed with exit {exitCode}.", ct);
                return;
            }

            var newSegments = await ParseChunkSegmentsAsync(sessionId, sessionDir, outputDir, chunkIndex, ct);
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
        string sessionId,
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

    private static string BuildChunkPath(string sessionDir, int chunkIndex)
        => Path.Combine(sessionDir, "chunks", $"chunk-{chunkIndex:D5}.wav");

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
        return template
            .Replace("{input}", QuoteForShell(inputPath), StringComparison.Ordinal)
            .Replace("{output}", QuoteForShell(outputDir), StringComparison.Ordinal)
            .Replace("{speakers}", speakers.ToString(), StringComparison.Ordinal)
            .Replace("{sessionId}", QuoteForShell(sessionId), StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunShellCommandAsync(string command, CancellationToken ct)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
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

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start shell process.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (process.ExitCode, stdOut, stdErr);
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
}
