using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WorkIntel.Configuration;
using WorkIntel.Integrations;
using WorkIntel.Intent;
using WorkIntel.Transcription;

namespace WorkIntel.UI;

/// <summary>
/// Modal preferences editor — six tabs (Capture / Whisper / LLM / Harvest /
/// Trello / Slack), inline "Test connection" buttons for each integration,
/// returns the edited <see cref="AppPreferences"/> on OK.
/// </summary>
/// <remarks>
/// Built programmatically (no .Designer.cs) so the file is self-contained.
/// Reads initial values into form controls on construction; reads them back
/// out into a fresh <see cref="AppPreferences"/> on OK.
/// </remarks>
public sealed class SettingsDialog : Form
{
    public AppPreferences Result { get; private set; }

    // Capture tab
    private NumericUpDown _idleAfterSec = null!;
    private NumericUpDown _vadActivation = null!;
    private NumericUpDown _vadDeactivation = null!;
    private NumericUpDown _vadHangoverMs = null!;

    // Whisper tab
    private ComboBox _whisperModel = null!;
    private TextBox  _whisperLang = null!;
    private TextBox  _whisperPrompt = null!;
    private CheckBox _whisperTranslate = null!;
    private NumericUpDown _whisperThreads = null!;

    // LLM tab
    private ComboBox _llmModel = null!;
    private NumericUpDown _llmContextSize = null!;
    private NumericUpDown _llmThreads = null!;
    private NumericUpDown _llmGpuLayers = null!;
    private NumericUpDown _llmTemperature = null!;

    // Harvest tab
    private TextBox _harvestAccountId = null!;
    private TextBox _harvestToken = null!;
    private NumericUpDown _harvestProjectId = null!;
    private NumericUpDown _harvestTaskId = null!;
    private Label _harvestStatus = null!;

    // Trello tab
    private TextBox _trelloKey = null!;
    private TextBox _trelloToken = null!;
    private TextBox _trelloListId = null!;
    private Label _trelloStatus = null!;

    // Slack tab
    private TextBox _slackToken = null!;
    private TextBox _slackChannel = null!;
    private Label _slackStatus = null!;

    private static readonly Color BgColor = Color.FromArgb(28, 30, 36);
    private static readonly Color FgColor = Color.FromArgb(220, 220, 230);
    private static readonly Color MutedColor = Color.FromArgb(140, 150, 165);
    private static readonly Color SuccessColor = Color.FromArgb(120, 230, 170);
    private static readonly Color ErrorColor = Color.FromArgb(240, 130, 130);

    public SettingsDialog(AppPreferences initial)
    {
        Result = initial; // replaced on OK

        Text = "WorkIntel — Settings";
        ClientSize = new Size(640, 520);
        MinimumSize = new Size(580, 460);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = BgColor;
        ForeColor = FgColor;
        Font = new Font("Segoe UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(8, 4) };
        tabs.TabPages.Add(BuildCaptureTab(initial.Capture));
        tabs.TabPages.Add(BuildWhisperTab(initial.Whisper));
        tabs.TabPages.Add(BuildLlmTab(initial.Llm));
        tabs.TabPages.Add(BuildHarvestTab(initial.Secrets.Harvest ?? new HarvestSecrets()));
        tabs.TabPages.Add(BuildTrelloTab(initial.Secrets.Trello ?? new TrelloSecrets()));
        tabs.TabPages.Add(BuildSlackTab(initial.Secrets.Slack ?? new SlackSecrets()));

        var buttons = BuildButtonRow();

        Controls.Add(tabs);
        Controls.Add(buttons);
    }

    // ───────── Capture ─────────
    private TabPage BuildCaptureTab(CapturePreferences c)
    {
        var grid = NewGrid();
        AddRow(grid, "Idle after (seconds)", _idleAfterSec = NewInt(5, 600, c.IdleAfterSeconds));
        AddRow(grid, "VAD activation (dBFS)", _vadActivation = NewDecimal(-90m, 0m, (decimal)c.VadActivationDb, 1));
        AddRow(grid, "VAD deactivation (dBFS)", _vadDeactivation = NewDecimal(-90m, 0m, (decimal)c.VadDeactivationDb, 1));
        AddRow(grid, "VAD hangover (ms)", _vadHangoverMs = NewInt(50, 5000, c.VadHangoverMs));
        AddNote(grid, "Lower dBFS values are more sensitive. Hangover keeps a segment open across short pauses.");
        return Tab("Capture", grid);
    }

    // ───────── Whisper ─────────
    private TabPage BuildWhisperTab(WhisperOptions w)
    {
        var grid = NewGrid();
        _whisperModel = NewEnumCombo<WhisperModel>(w.Model);
        AddRow(grid, "Model", _whisperModel);
        AddRow(grid, "Language", _whisperLang = NewText(w.Language, 16));
        AddRow(grid, "Initial prompt (optional)", _whisperPrompt = NewText(w.InitialPrompt ?? "", 60));
        AddRow(grid, "Translate to English", _whisperTranslate = NewBool(w.Translate));
        AddRow(grid, "Threads", _whisperThreads = NewInt(1, 64, w.Threads));
        AddNote(grid, "Model changes require a restart. Language: \"auto\" detects per segment.");
        return Tab("Whisper", grid);
    }

    // ───────── LLM ─────────
    private TabPage BuildLlmTab(LocalLlmOptions l)
    {
        var grid = NewGrid();
        _llmModel = NewEnumCombo<LocalLlmModel>(l.Model);
        AddRow(grid, "Model", _llmModel);
        AddRow(grid, "Context size (tokens)", _llmContextSize = NewInt(512, 32_768, (int)l.ContextSize));
        AddRow(grid, "Threads", _llmThreads = NewInt(1, 64, l.Threads));
        AddRow(grid, "GPU offload layers", _llmGpuLayers = NewInt(0, 100, l.GpuLayerCount));
        AddRow(grid, "Sampling temperature", _llmTemperature = NewDecimal(0m, 2m, (decimal)l.Temperature, 2));
        AddNote(grid, "Phi-3.5 Mini has 33 layers — set GPU offload to 33 for full GPU inference. Model changes require a restart.");
        return Tab("Phi-3.5 LLM", grid);
    }

    // ───────── Harvest ─────────
    private TabPage BuildHarvestTab(HarvestSecrets s)
    {
        var grid = NewGrid();
        AddRow(grid, "Account ID", _harvestAccountId = NewText(s.AccountId ?? "", 30));
        AddSecretRow(grid, "Access token", _harvestToken = NewSecret(s.AccessToken));
        AddRow(grid, "Default project ID", _harvestProjectId = NewLong(0, long.MaxValue, s.DefaultProjectId ?? 0));
        AddRow(grid, "Default task ID",    _harvestTaskId    = NewLong(0, long.MaxValue, s.DefaultTaskId ?? 0));
        AddTestRow(grid, "Test connection", _harvestStatus = NewStatus(),
            async ct => await new HarvestClient(ReadHarvest()).TestAsync(ct));
        return Tab("Harvest", grid);
    }

    // ───────── Trello ─────────
    private TabPage BuildTrelloTab(TrelloSecrets s)
    {
        var grid = NewGrid();
        AddSecretRow(grid, "API key", _trelloKey = NewSecret(s.ApiKey));
        AddSecretRow(grid, "Token", _trelloToken = NewSecret(s.Token));
        AddRow(grid, "Default list ID", _trelloListId = NewText(s.DefaultListId ?? "", 30));
        AddTestRow(grid, "Test connection", _trelloStatus = NewStatus(),
            async ct => await new TrelloClient(ReadTrello()).TestAsync(ct));
        return Tab("Trello", grid);
    }

    // ───────── Slack ─────────
    private TabPage BuildSlackTab(SlackSecrets s)
    {
        var grid = NewGrid();
        AddSecretRow(grid, "Bot token (xoxb-…)", _slackToken = NewSecret(s.BotToken));
        AddRow(grid, "Default channel", _slackChannel = NewText(s.DefaultChannel ?? "#general", 24));
        AddTestRow(grid, "Test connection", _slackStatus = NewStatus(),
            async ct => await new SlackClient(ReadSlack()).TestAsync(ct));
        return Tab("Slack", grid);
    }

    // ───────── Buttons ─────────
    private Panel BuildButtonRow()
    {
        var row = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(22, 23, 28), Padding = new Padding(12, 8, 12, 8) };

        var ok = new Button
        {
            Text = "Save",
            Size = new Size(96, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.FromArgb(46, 120, 200),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(70, 140, 220);
        ok.Click += (_, _) =>
        {
            try
            {
                Result = ReadFormToPreferences();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Size = new Size(96, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.FromArgb(60, 64, 72),
            ForeColor = FgColor,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(85, 90, 100);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        // Position right-aligned: cancel left of ok.
        ok.Location = new Point(row.ClientSize.Width - ok.Width - 12, 8);
        cancel.Location = new Point(ok.Location.X - cancel.Width - 8, 8);
        row.Resize += (_, _) =>
        {
            ok.Location = new Point(row.ClientSize.Width - ok.Width - 12, 8);
            cancel.Location = new Point(ok.Location.X - cancel.Width - 8, 8);
        };

        AcceptButton = ok;
        CancelButton = cancel;

        row.Controls.Add(cancel);
        row.Controls.Add(ok);
        return row;
    }

    // ───────── Form → Preferences ─────────
    private AppPreferences ReadFormToPreferences()
    {
        return new AppPreferences
        {
            SchemaVersion = "1",
            Capture = new CapturePreferences
            {
                IdleAfterSeconds   = (int)_idleAfterSec.Value,
                VadActivationDb    = (float)_vadActivation.Value,
                VadDeactivationDb  = (float)_vadDeactivation.Value,
                VadHangoverMs      = (int)_vadHangoverMs.Value,
            },
            Whisper = new WhisperOptions
            {
                Model         = (WhisperModel)_whisperModel.SelectedItem!,
                Language      = string.IsNullOrWhiteSpace(_whisperLang.Text) ? "auto" : _whisperLang.Text.Trim(),
                InitialPrompt = string.IsNullOrWhiteSpace(_whisperPrompt.Text) ? null : _whisperPrompt.Text,
                Translate     = _whisperTranslate.Checked,
                Threads       = (int)_whisperThreads.Value,
            },
            Llm = new LocalLlmOptions
            {
                Model           = (LocalLlmModel)_llmModel.SelectedItem!,
                ContextSize     = (uint)_llmContextSize.Value,
                Threads         = (int)_llmThreads.Value,
                GpuLayerCount   = (int)_llmGpuLayers.Value,
                Temperature     = (float)_llmTemperature.Value,
            },
            Secrets = new IntegrationSecrets
            {
                Harvest = ReadHarvest(),
                Trello  = ReadTrello(),
                Slack   = ReadSlack(),
            }
        };
    }

    private HarvestSecrets ReadHarvest() => new()
    {
        AccountId        = NullIfBlank(_harvestAccountId.Text),
        AccessToken      = NullIfBlank(_harvestToken.Text),
        DefaultProjectId = _harvestProjectId.Value > 0 ? (long)_harvestProjectId.Value : null,
        DefaultTaskId    = _harvestTaskId.Value    > 0 ? (long)_harvestTaskId.Value    : null,
    };

    private TrelloSecrets ReadTrello() => new()
    {
        ApiKey        = NullIfBlank(_trelloKey.Text),
        Token         = NullIfBlank(_trelloToken.Text),
        DefaultListId = NullIfBlank(_trelloListId.Text),
    };

    private SlackSecrets ReadSlack() => new()
    {
        BotToken       = NullIfBlank(_slackToken.Text),
        DefaultChannel = NullIfBlank(_slackChannel.Text) ?? "#general",
    };

    // ───────── Control factories ─────────
    private static TableLayoutPanel NewGrid() => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        AutoSize = false,
        Padding = new Padding(16, 12, 16, 12),
        ColumnStyles = { new ColumnStyle(SizeType.Absolute, 180), new ColumnStyle(SizeType.Percent, 100) },
        BackColor = BgColor,
        ForeColor = FgColor,
    };

    private static TabPage Tab(string title, Control body)
    {
        var page = new TabPage(title) { BackColor = BgColor, ForeColor = FgColor, Padding = new Padding(0) };
        page.Controls.Add(body);
        return page;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control input)
    {
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new Label
        {
            Text = label,
            ForeColor = FgColor,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false,
            Width = 160,
            Height = 26,
            Margin = new Padding(0, 4, 12, 4),
        };
        input.Margin = new Padding(0, 4, 0, 4);
        if (input is TextBox || input is ComboBox)
        {
            input.Width = 380;
        }
        grid.Controls.Add(lbl, 0, grid.RowCount - 1);
        grid.Controls.Add(input, 1, grid.RowCount - 1);
    }

    private static void AddSecretRow(TableLayoutPanel grid, string label, TextBox secretBox)
    {
        // Wrap secret + show-toggle in a panel so the row stays one line.
        var wrap = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        secretBox.Width = 320;
        var show = new CheckBox { Text = "Show", AutoSize = true, ForeColor = MutedColor, Margin = new Padding(8, 4, 0, 0) };
        show.CheckedChanged += (_, _) => secretBox.UseSystemPasswordChar = !show.Checked;
        wrap.Controls.Add(secretBox);
        wrap.Controls.Add(show);
        AddRow(grid, label, wrap);
    }

    private void AddTestRow(TableLayoutPanel grid, string label, Label statusLabel, Func<CancellationToken, Task<string>> probe)
    {
        var btn = new Button
        {
            Text = label,
            AutoSize = true,
            BackColor = Color.FromArgb(60, 64, 72),
            ForeColor = FgColor,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 8, 0, 0),
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(85, 90, 100);

        async void OnClick(object? _, EventArgs __)
        {
            btn.Enabled = false;
            statusLabel.Text = "Testing…";
            statusLabel.ForeColor = MutedColor;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var result = await probe(cts.Token).ConfigureAwait(true);
                statusLabel.Text = "✓ " + result;
                statusLabel.ForeColor = SuccessColor;
            }
            catch (Exception ex)
            {
                statusLabel.Text = "✗ " + ex.Message;
                statusLabel.ForeColor = ErrorColor;
            }
            finally
            {
                btn.Enabled = true;
            }
        }
        btn.Click += OnClick;

        var wrap = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        wrap.Controls.Add(btn);
        wrap.Controls.Add(statusLabel);
        AddRow(grid, "", wrap);
    }

    private static void AddNote(TableLayoutPanel grid, string text)
    {
        var lbl = new Label
        {
            Text = text,
            ForeColor = MutedColor,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 12, 0, 0),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
        };
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "" }, 0, grid.RowCount - 1);
        grid.Controls.Add(lbl, 1, grid.RowCount - 1);
    }

    private static NumericUpDown NewInt(int min, int max, int value) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
        Width = 120, Margin = new Padding(0, 4, 0, 4),
        ThousandsSeparator = false,
    };

    private static NumericUpDown NewLong(long min, long max, long value) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
        Width = 200, Margin = new Padding(0, 4, 0, 4),
        ThousandsSeparator = false,
    };

    private static NumericUpDown NewDecimal(decimal min, decimal max, decimal value, int places) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
        DecimalPlaces = places, Increment = places switch { 1 => 1m, 2 => 0.05m, _ => 0.1m },
        Width = 120, Margin = new Padding(0, 4, 0, 4),
    };

    private static TextBox NewText(string value, int charWidth)
    {
        var tb = new TextBox { Text = value, Width = Math.Min(380, charWidth * 9), BackColor = Color.FromArgb(40, 42, 50), ForeColor = FgColor, BorderStyle = BorderStyle.FixedSingle };
        return tb;
    }

    private static TextBox NewSecret(string? value) => new()
    {
        Text = value ?? "",
        UseSystemPasswordChar = true,
        BackColor = Color.FromArgb(40, 42, 50),
        ForeColor = FgColor,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static CheckBox NewBool(bool value) => new()
    {
        Checked = value, AutoSize = true, ForeColor = FgColor, Margin = new Padding(0, 4, 0, 4),
    };

    private static ComboBox NewEnumCombo<TEnum>(TEnum selected) where TEnum : struct, Enum
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240,
            BackColor = Color.FromArgb(40, 42, 50),
            ForeColor = FgColor,
            FlatStyle = FlatStyle.Flat,
        };
        foreach (var v in Enum.GetValues<TEnum>()) cb.Items.Add(v);
        cb.SelectedItem = selected;
        return cb;
    }

    private static Label NewStatus() => new()
    {
        Text = "",
        AutoSize = true,
        ForeColor = MutedColor,
        Margin = new Padding(12, 12, 0, 0),
        MaximumSize = new Size(360, 0),
    };

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
