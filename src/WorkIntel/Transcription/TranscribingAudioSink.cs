using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;
using WorkIntel.Audio;
using WorkIntel.Contracts.V1;
using WorkIntel.Pipeline;
using WorkIntel.Tasks;

namespace WorkIntel.Transcription;

public sealed class TasksExtractedEventArgs : EventArgs
{
    public TranscriptionResult Transcription { get; }
    public IReadOnlyList<TaskCandidate> Tasks { get; }
    public DateTimeOffset At { get; }
    public TasksExtractedEventArgs(TranscriptionResult transcription, IReadOnlyList<TaskCandidate> tasks)
    {
        Transcription = transcription;
        Tasks = tasks;
        At = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// <see cref="IAudioSink"/> that runs each segment through an <see cref="ITranscriber"/>,
/// then through an <see cref="IIntentExtractor"/> (now producing <see cref="TaskCandidate"/>s),
/// and persists the results to the remote task store.
/// </summary>
/// <remarks>
/// <para>
/// Failures are swallowed and logged — the audio pipeline must keep flowing
/// even if the LLM or the backend has a bad moment.
/// </para>
/// <para>
/// Two events fire for the UI:
/// <list type="bullet">
///   <item><see cref="Transcribed"/> — every successful transcript, even when no tasks are extracted.</item>
///   <item><see cref="TasksExtracted"/> — only when the LLM emits one or more candidates.</item>
/// </list>
/// The TaskListPanel doesn't subscribe to <see cref="TasksExtracted"/> directly — it gets
/// the same data round-tripped through the backend's <c>StreamTaskEvents</c>, which means
/// what the panel shows is exactly what's in the DB. The event exists for the live log to
/// display "extracted but not yet persisted" lines for debugging.
/// </para>
/// </remarks>
public sealed class TranscribingAudioSink : IAudioSink
{
    private readonly ITranscriber _transcriber;
    private readonly IIntentExtractor? _extractor;
    private readonly RemoteTaskStore? _taskStore;

    public event EventHandler<TranscriptionResult>? Transcribed;
    public event EventHandler<TasksExtractedEventArgs>? TasksExtracted;

    public TranscribingAudioSink(
        ITranscriber transcriber,
        IIntentExtractor? extractor = null,
        RemoteTaskStore? taskStore = null)
    {
        _transcriber = transcriber;
        _extractor = extractor;
        _taskStore = taskStore;
    }

    public async ValueTask PushAsync(SpeechSegment segment, CancellationToken ct)
    {
        TranscriptionResult? result;
        try
        {
            result = await _transcriber.TranscribeAsync(segment, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"transcription failed for {segment}", ex);
            return;
        }

        if (result is null || string.IsNullOrWhiteSpace(result.Text))
        {
            Log.Debug($"empty transcript for {segment}; dropping");
            return;
        }

        Log.Info($"[{result.Language}] {result.Text}");
        Transcribed?.Invoke(this, result);

        if (_extractor is null) return;

        IReadOnlyList<TaskCandidate> tasks;
        try
        {
            tasks = await _extractor.ExtractAsync(result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("task extraction failed", ex);
            return;
        }

        if (tasks is null || tasks.Count == 0) return;

        TasksExtracted?.Invoke(this, new TasksExtractedEventArgs(result, tasks));

        if (_taskStore is null)
        {
            foreach (var t in tasks) Log.Info($"task (unpersisted): \"{t.Title}\" conf={t.Confidence:F2}");
            return;
        }

        // Persist each candidate. Network/backend failures don't break the audio path.
        var sourceMeta = new Dictionary<string, string?>
        {
            ["segment_started_at"] = segment.StartedAt.ToString("O"),
            ["segment_duration_sec"] = segment.Duration.TotalSeconds.ToString("F3"),
            ["language"] = result.Language,
        };

        foreach (var t in tasks)
        {
            try
            {
                var saved = await _taskStore.CreateAsync(t, TaskSource.Audio, sourceMeta, result.Text, ct).ConfigureAwait(false);
                Log.Info($"task saved {saved.Id} \"{Truncate(saved.Title, 80)}\"");
            }
            catch (Exception ex)
            {
                Log.Warn($"task save failed (backend down?): {ex.Message}");
                // No retry/queue yet — if the backend is down, the task is lost.
                // Phase 11-ish: add a local outbox.
            }
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
