using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;
using WorkIntel.Audio;
using WorkIntel.Pipeline;

namespace WorkIntel.Transcription;

public sealed class IntentsDetectedEventArgs : EventArgs
{
    public TranscriptionResult Transcription { get; }
    public IReadOnlyList<DetectedIntent> Intents { get; }
    public DateTimeOffset DetectedAt { get; }
    public IntentsDetectedEventArgs(TranscriptionResult transcription, IReadOnlyList<DetectedIntent> intents)
    {
        Transcription = transcription;
        Intents = intents;
        DetectedAt = DateTimeOffset.UtcNow;
    }
}

public sealed class DispatchOutcomeEventArgs : EventArgs
{
    public DetectedIntent Intent { get; }
    public DispatchResult Result { get; }
    public DateTimeOffset At { get; }
    public DispatchOutcomeEventArgs(DetectedIntent intent, DispatchResult result)
    {
        Intent = intent;
        Result = result;
        At = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// <see cref="IAudioSink"/> that runs each segment through an <see cref="ITranscriber"/>
/// and forwards the result down the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The sink swallows transcription failures rather than propagating: the audio path
/// must keep flowing even if the model failed to load or a single utterance hit a
/// runtime error. Errors are logged.
/// </para>
/// <para>
/// Once <see cref="IIntentExtractor"/> and <see cref="IIntegrationDispatcher"/> are
/// implemented, plug them in via the optional constructor params; the chain is
/// linear: transcribe → extract intents → dispatch.
/// </para>
/// </remarks>
public sealed class TranscribingAudioSink : IAudioSink
{
    private readonly ITranscriber _transcriber;
    private readonly IIntentExtractor? _intentExtractor;
    private readonly IIntegrationDispatcher? _dispatcher;

    public event EventHandler<TranscriptionResult>? Transcribed;
    public event EventHandler<IntentsDetectedEventArgs>? IntentsDetected;
    public event EventHandler<DispatchOutcomeEventArgs>? DispatchOutcome;

    public TranscribingAudioSink(
        ITranscriber transcriber,
        IIntentExtractor? intentExtractor = null,
        IIntegrationDispatcher? dispatcher = null)
    {
        _transcriber = transcriber;
        _intentExtractor = intentExtractor;
        _dispatcher = dispatcher;
    }

    public async ValueTask PushAsync(SpeechSegment segment, CancellationToken ct)
    {
        TranscriptionResult? result;
        try
        {
            result = await _transcriber.TranscribeAsync(segment, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
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

        // Phase 3 hand-off (no-op until IIntentExtractor is implemented).
        if (_intentExtractor is null) return;

        try
        {
            var intents = await _intentExtractor.ExtractAsync(result, ct).ConfigureAwait(false);
            if (intents is null || intents.Count == 0) return;

            // Surface to subscribers (e.g. the live UI) before / regardless of dispatch.
            IntentsDetected?.Invoke(this, new IntentsDetectedEventArgs(result, intents));

            if (_dispatcher is null)
            {
                foreach (var intent in intents)
                {
                    Log.Info($"intent (undispatched): {intent.Kind} :: {intent.SourceQuote}");
                    var soft = new DispatchResult(false, "no dispatcher configured", null);
                    DispatchOutcome?.Invoke(this, new DispatchOutcomeEventArgs(intent, soft));
                }
                return;
            }

            foreach (var intent in intents)
            {
                DispatchResult dispatch;
                try
                {
                    dispatch = await _dispatcher.DispatchAsync(intent, ct).ConfigureAwait(false);
                    Log.Info($"dispatch {intent.Kind} → success={dispatch.Success}, msg={dispatch.Message}, url={dispatch.RemoteUrl}");
                }
                catch (Exception ex)
                {
                    Log.Error($"dispatch failed for {intent.Kind}", ex);
                    dispatch = new DispatchResult(false, ex.Message, null);
                }
                DispatchOutcome?.Invoke(this, new DispatchOutcomeEventArgs(intent, dispatch));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("intent extraction failed", ex);
        }
    }
}
