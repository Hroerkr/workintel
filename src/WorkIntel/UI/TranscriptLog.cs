using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using WorkIntel.App;
using WorkIntel.Pipeline;
using WorkIntel.Tasks;

namespace WorkIntel.UI;

/// <summary>
/// Live append-only log of pipeline events — transcripts, detected intents,
/// state transitions, dispatch outcomes. Owns its own <see cref="RichTextBox"/>,
/// formatting policy, color palette, and trim-on-overflow logic. UI thread
/// marshalling is internal; callers can fire events from any thread without
/// thinking about <c>Invoke</c>.
/// </summary>
/// <remarks>
/// Extracted from <see cref="MainWindow"/> so the upcoming "show dispatch
/// outcome inline next to each intent" feature lands in a focused control
/// rather than threading through the form. Tests aren't worth writing for
/// pure WinForms styling code, but the API is shaped so a snapshot-style
/// test could verify "this sequence of calls produces N runs of these colors"
/// later if needed.
/// </remarks>
public sealed class TranscriptLog : UserControl
{
    private const int MaxLogChars = 200_000;

    public static readonly Color BgColor          = Color.FromArgb(15, 16, 20);
    public static readonly Color FgColor          = Color.FromArgb(220, 220, 230);
    public static readonly Color MutedColor       = Color.FromArgb(140, 150, 165);
    public static readonly Color AccentTranscript = Color.FromArgb(170, 220, 255);
    public static readonly Color AccentIntent     = Color.FromArgb(120, 230, 170);
    public static readonly Color AccentSuccess    = Color.FromArgb(120, 230, 170);
    public static readonly Color AccentError      = Color.FromArgb(240, 130, 130);

    private readonly RichTextBox _box;
    private readonly SynchronizationContext _uiContext;

    public TranscriptLog()
    {
        Dock = DockStyle.Fill;
        BackColor = BgColor;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = BgColor,
            ForeColor = FgColor,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            DetectUrls = false,
            HideSelection = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Font = new Font("Cascadia Mono", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            WordWrap = true,
        };
        // Cascadia Mono ships with Windows 11 / Terminal; gracefully fall back if missing.
        if (_box.Font.Name != "Cascadia Mono")
            _box.Font = new Font("Consolas", 9.5f);

        Controls.Add(_box);
    }

    // ─── Public, type-safe append methods ─────────────────────────────────

    public void AppendBanner(string text)
    {
        Post(() => WriteLine(text, MutedColor, italic: true));
    }

    public void AppendBlankLine()
    {
        Post(() => WriteLine(string.Empty, FgColor));
    }

    public void AppendStateChange(AppState oldState, AppState newState)
    {
        Post(() => WriteLine($"[state] {oldState} → {newState}", MutedColor, italic: true));
    }

    public void AppendTranscript(string language, string text)
    {
        Post(() =>
        {
            string ts = DateTimeOffset.Now.ToString("HH:mm:ss");
            WriteInline($"{ts}  ", MutedColor);
            WriteInline($"[{language}] ", MutedColor, italic: true);
            WriteInline(text, AccentTranscript);
            EndLine();
        });
    }

    public void AppendIntent(DetectedIntent intent)
    {
        // Vestigial — kept compilable for the dispatcher path, not called on the new pipeline.
        Post(() =>
        {
            WriteInline("   → ", AccentIntent);
            WriteInline(intent.Kind, AccentIntent, bold: true);
            WriteInline($"  (conf {intent.Confidence:F2})", MutedColor);
            EndLine();
        });
    }

    public void AppendTask(TaskCandidate task)
    {
        Post(() =>
        {
            WriteInline("   → ", AccentIntent);
            WriteInline(task.Title, AccentIntent, bold: true);
            WriteInline($"  (conf {task.Confidence:F2})", MutedColor);
            if (!string.IsNullOrWhiteSpace(task.Owner))
            {
                WriteInline("  owner=", MutedColor);
                WriteInline(task.Owner, FgColor);
            }
            if (!string.IsNullOrWhiteSpace(task.Deadline))
            {
                WriteInline("  deadline=", MutedColor);
                WriteInline(task.Deadline, FgColor);
            }
            EndLine();
        });
    }

    /// <summary>Forward-looking — the dispatcher → UI feedback pass will call this after each
    /// detected intent has run through <see cref="IIntegrationDispatcher"/>.</summary>
    public void AppendDispatchOutcome(string intentKind, bool success, string? message, string? remoteUrl)
    {
        Post(() =>
        {
            var color = success ? AccentSuccess : AccentError;
            WriteInline("       ", FgColor);
            WriteInline(success ? "✓" : "✗", color, bold: true);
            WriteInline($" {intentKind}", color);
            if (!string.IsNullOrWhiteSpace(message))
                WriteInline($" — {message}", MutedColor);
            if (!string.IsNullOrWhiteSpace(remoteUrl))
                WriteInline($"  [{remoteUrl}]", MutedColor, italic: true);
            EndLine();
        });
    }

    public void AppendError(string message)
    {
        Post(() => WriteLine(message, AccentError));
    }

    public void Clear()
    {
        Post(() => _box.Clear());
    }

    // ─── Plumbing ────────────────────────────────────────────────────────

    private void Post(Action a)
    {
        if (IsDisposed) return;
        _uiContext.Post(_ =>
        {
            if (IsDisposed || _box.IsDisposed) return;
            try { a(); }
            catch (Exception ex) { Log.Error("TranscriptLog UI update failed", ex); }
        }, null);
    }

    private void WriteLine(string text, Color color, bool bold = false, bool italic = false)
    {
        WriteInline(text, color, bold, italic);
        EndLine();
    }

    private void WriteInline(string text, Color color, bool bold = false, bool italic = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        int start = _box.TextLength;
        _box.AppendText(text);
        _box.Select(start, text.Length);
        _box.SelectionColor = color;
        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;
        _box.SelectionFont = new Font(_box.Font, style);
        _box.Select(_box.TextLength, 0);
    }

    private void EndLine()
    {
        _box.AppendText(Environment.NewLine);
        TrimIfHuge();
        ScrollToEnd();
    }

    private void TrimIfHuge()
    {
        if (_box.TextLength <= MaxLogChars) return;
        // Drop the first 25% — keeps recent context, avoids freezing the UI on a giant RichTextBox.
        int dropTo = _box.TextLength / 4;
        _box.Select(0, dropTo);
        _box.SelectedText = string.Empty;
    }

    private void ScrollToEnd()
    {
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
    }
}
