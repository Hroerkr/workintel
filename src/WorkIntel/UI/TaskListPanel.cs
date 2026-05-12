using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WorkIntel.App;
using WorkIntel.Contracts.V1;
using WorkIntel.Tasks;
using ProtoTaskStatus = WorkIntel.Contracts.V1.TaskStatus;

namespace WorkIntel.UI;

/// <summary>
/// Live, sortable list of task rows from the backend, populated by
/// <see cref="RemoteTaskStore.ListAsync"/> on load and kept fresh by the
/// <see cref="RemoteTaskStore.TaskChanged"/> stream.
/// </summary>
public sealed class TaskListPanel : UserControl
{
    private static readonly Color BgColor      = Color.FromArgb(22, 23, 28);
    private static readonly Color FgColor      = Color.FromArgb(220, 220, 230);
    private static readonly Color MutedColor   = Color.FromArgb(140, 150, 165);
    private static readonly Color HeaderBg     = Color.FromArgb(34, 36, 42);

    private readonly RemoteTaskStore _store;
    private readonly SynchronizationContext _uiContext;

    private readonly ToolStrip _toolbar = new();
    private readonly ListView _list = new();
    private readonly Label _statusLabel = new();
    private readonly Dictionary<string, ListViewItem> _rowsById = new();
    private readonly List<ToolStripButton> _filterButtons = new();

    private ProtoTaskStatus? _filter;

    public TaskListPanel(RemoteTaskStore store)
    {
        _store = store;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Dock = DockStyle.Fill;
        BackColor = BgColor;
        ForeColor = FgColor;

        BuildToolbar();
        BuildList();
        BuildStatus();

        Controls.Add(_list);          // fill
        Controls.Add(_toolbar);       // top
        Controls.Add(_statusLabel);   // bottom

        _store.TaskChanged += OnTaskChanged;
        _store.StreamStateChanged += OnStreamStateChanged;

        Load += async (_, _) =>
        {
            _store.StartEventStream();
            await ReloadAsync().ConfigureAwait(false);
        };
    }

    // ─── Layout ──────────────────────────────────────────────────────────

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.BackColor = HeaderBg;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(6, 4, 6, 4);
        _toolbar.RenderMode = ToolStripRenderMode.System;

        AddFilterButton("All", null);
        AddFilterButton("Pending", ProtoTaskStatus.Pending);
        AddFilterButton("Included", ProtoTaskStatus.Included);
        AddFilterButton("Excluded", ProtoTaskStatus.Excluded);
        AddFilterButton("Exported", ProtoTaskStatus.Exported);

        _toolbar.Items.Add(new ToolStripSeparator());

        var includeBtn = new ToolStripButton("Include") { ForeColor = FgColor };
        includeBtn.Click += (_, _) => SetSelectedStatus(ProtoTaskStatus.Included);
        _toolbar.Items.Add(includeBtn);

        var excludeBtn = new ToolStripButton("Exclude") { ForeColor = FgColor };
        excludeBtn.Click += (_, _) => SetSelectedStatus(ProtoTaskStatus.Excluded);
        _toolbar.Items.Add(excludeBtn);

        var removeBtn = new ToolStripButton("Remove") { ForeColor = FgColor };
        removeBtn.Click += (_, _) => DeleteSelected();
        _toolbar.Items.Add(removeBtn);

        _toolbar.Items.Add(new ToolStripSeparator());

        var reloadBtn = new ToolStripButton("Reload") { ForeColor = MutedColor };
        reloadBtn.Click += async (_, _) => await ReloadAsync().ConfigureAwait(false);
        _toolbar.Items.Add(reloadBtn);
    }

    private void AddFilterButton(string label, ProtoTaskStatus? filter)
    {
        var btn = new ToolStripButton(label)
        {
            CheckOnClick = false,
            ForeColor = filter == _filter ? FgColor : MutedColor,
            Tag = filter,
        };
        btn.Click += async (sender, _) =>
        {
            // Tag is either null ("All") or a boxed ProtoTaskStatus.
            _filter = (ProtoTaskStatus?)((ToolStripButton)sender!).Tag;
            RecolorFilterButtons();
            await ReloadAsync().ConfigureAwait(false);
        };
        _filterButtons.Add(btn);
        _toolbar.Items.Add(btn);
    }

    private void RecolorFilterButtons()
    {
        foreach (var b in _filterButtons)
        {
            var bFilter = (ProtoTaskStatus?)b.Tag;
            b.ForeColor = bFilter == _filter ? FgColor : MutedColor;
        }
    }

    private void BuildList()
    {
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = false;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.BackColor = BgColor;
        _list.ForeColor = FgColor;
        _list.BorderStyle = BorderStyle.None;
        _list.Font = new Font("Segoe UI", 9f);

        _list.Columns.Add("Source", 70);
        _list.Columns.Add("When", 110);
        _list.Columns.Add("Title", 360);
        _list.Columns.Add("Owner", 90);
        _list.Columns.Add("Deadline", 100);
        _list.Columns.Add("Conf", 50);
        _list.Columns.Add("Status", 80);

        // Right-click context menu mirrors the toolbar actions.
        var ctx = new ContextMenuStrip();
        ctx.Items.Add("Include", null, (_, _) => SetSelectedStatus(ProtoTaskStatus.Included));
        ctx.Items.Add("Exclude", null, (_, _) => SetSelectedStatus(ProtoTaskStatus.Excluded));
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Remove", null, (_, _) => DeleteSelected());
        _list.ContextMenuStrip = ctx;
    }

    private void BuildStatus()
    {
        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = 22;
        _statusLabel.BackColor = HeaderBg;
        _statusLabel.ForeColor = MutedColor;
        _statusLabel.Padding = new Padding(8, 4, 8, 0);
        _statusLabel.Text = "Connecting…";
    }

    // ─── Data ────────────────────────────────────────────────────────────

    private async Task ReloadAsync()
    {
        try
        {
            var tasks = await _store.ListAsync(_filter).ConfigureAwait(false);
            Post(() =>
            {
                _list.BeginUpdate();
                _list.Items.Clear();
                _rowsById.Clear();
                foreach (var t in tasks) AddOrUpdateRow(t);
                _list.EndUpdate();
                _statusLabel.Text = $"{tasks.Count} task{(tasks.Count == 1 ? "" : "s")}";
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"task list reload failed: {ex.Message}");
            Post(() => _statusLabel.Text = $"Backend unreachable ({ex.GetType().Name})");
        }
    }

    private void OnTaskChanged(object? sender, TaskEvent evt)
    {
        Post(() =>
        {
            switch (evt.Type)
            {
                case TaskEventType.Created:
                case TaskEventType.Updated:
                    if (PassesFilter(evt.Task)) AddOrUpdateRow(evt.Task);
                    else if (_rowsById.TryGetValue(evt.Task.Id, out var existing)) RemoveRow(existing);
                    break;
                case TaskEventType.Deleted:
                    if (_rowsById.TryGetValue(evt.Task.Id, out var row)) RemoveRow(row);
                    break;
            }
            _statusLabel.Text = $"{_list.Items.Count} task{(_list.Items.Count == 1 ? "" : "s")}";
        });
    }

    private void OnStreamStateChanged(object? sender, StreamState state)
    {
        Post(() =>
        {
            _statusLabel.Text = state switch
            {
                StreamState.Connecting => "Connecting to backend…",
                StreamState.Connected => $"Live ({_list.Items.Count} task{(_list.Items.Count == 1 ? "" : "s")})",
                StreamState.Disconnected => "Backend disconnected — retrying…",
                _ => _statusLabel.Text,
            };
        });
    }

    private bool PassesFilter(TaskItem t) =>
        _filter is null || t.Status == _filter;

    private void AddOrUpdateRow(TaskItem t)
    {
        if (_rowsById.TryGetValue(t.Id, out var existing))
        {
            UpdateRowFromTask(existing, t);
            return;
        }

        var row = new ListViewItem(new[]
        {
            SourceGlyph(t.Source),
            FormatRelative(t.DetectedAt?.ToDateTime() ?? DateTime.UtcNow),
            Truncate(t.Title, 80),
            t.HasOwner ? t.Owner : "",
            t.HasDeadline ? t.Deadline : "",
            $"{t.Confidence:F2}",
            t.Status.ToString().Replace("Task", "").ToLowerInvariant(),
        })
        {
            Tag = t.Id,
            UseItemStyleForSubItems = false,
        };

        ColorizeRow(row, t);
        _list.Items.Insert(0, row); // newest at top
        _rowsById[t.Id] = row;
    }

    private void UpdateRowFromTask(ListViewItem row, TaskItem t)
    {
        row.SubItems[0].Text = SourceGlyph(t.Source);
        row.SubItems[1].Text = FormatRelative(t.DetectedAt?.ToDateTime() ?? DateTime.UtcNow);
        row.SubItems[2].Text = Truncate(t.Title, 80);
        row.SubItems[3].Text = t.HasOwner ? t.Owner : "";
        row.SubItems[4].Text = t.HasDeadline ? t.Deadline : "";
        row.SubItems[5].Text = $"{t.Confidence:F2}";
        row.SubItems[6].Text = t.Status.ToString().Replace("Task", "").ToLowerInvariant();
        ColorizeRow(row, t);
    }

    private void RemoveRow(ListViewItem row)
    {
        _list.Items.Remove(row);
        if (row.Tag is string id) _rowsById.Remove(id);
    }

    private static void ColorizeRow(ListViewItem row, TaskItem t)
    {
        var color = t.Status switch
        {
            ProtoTaskStatus.Excluded => Color.FromArgb(120, 130, 145),
            ProtoTaskStatus.Removed  => Color.FromArgb(120, 130, 145),
            ProtoTaskStatus.Included => Color.FromArgb(120, 230, 170),
            ProtoTaskStatus.Exported => Color.FromArgb(170, 220, 255),
            _                        => FgColor,
        };
        foreach (ListViewItem.ListViewSubItem si in row.SubItems) si.ForeColor = color;
    }

    private static string SourceGlyph(TaskSource s) => s switch
    {
        TaskSource.Audio => "audio",
        TaskSource.Slack => "slack",
        _ => "other",
    };

    private static string FormatRelative(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return utc.ToLocalTime().ToString("MMM d HH:mm");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    // ─── Selection actions ───────────────────────────────────────────────

    private async void SetSelectedStatus(ProtoTaskStatus status)
    {
        var ids = new List<string>();
        foreach (ListViewItem item in _list.SelectedItems)
            if (item.Tag is string id) ids.Add(id);

        foreach (var id in ids)
        {
            try { await _store.UpdateStatusAsync(id, status).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"status update {id} → {status} failed: {ex.Message}"); }
        }
    }

    private async void DeleteSelected()
    {
        var ids = new List<string>();
        foreach (ListViewItem item in _list.SelectedItems)
            if (item.Tag is string id) ids.Add(id);

        foreach (var id in ids)
        {
            try { await _store.DeleteAsync(id).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"delete {id} failed: {ex.Message}"); }
        }
    }

    private void Post(Action a)
    {
        if (IsDisposed) return;
        _uiContext.Post(_ =>
        {
            if (IsDisposed) return;
            try { a(); }
            catch (Exception ex) { Log.Error("TaskListPanel UI update failed", ex); }
        }, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _store.TaskChanged -= OnTaskChanged;
            _store.StreamStateChanged -= OnStreamStateChanged;
        }
        base.Dispose(disposing);
    }
}
