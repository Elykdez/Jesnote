using System.Drawing;
using System.Windows.Forms;
using Jasnote.Core;

namespace Jasnote.Controls;

public sealed class SelectionBar : UserControl, ILocalizable
{
    readonly FlowLayoutPanel _path =
        new()
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true,
        };
    readonly Button _jump = new() { Text = "→", Width = 32 };
    readonly Button _copyKey = new() { Text = "⧉", Width = 32 };
    readonly ToolTip _tips = new();
    JsonTreeDocument? _currentDoc;

    public event EventHandler<int>? NodeRequested;
    public event EventHandler? JumpRequested;
    public event EventHandler? CopyKeyRequested;

    public int SelectedId { get; private set; } = -1;

    public SelectionBar()
    {
        Height = 28;
        Dock = DockStyle.Top;
        Padding = new Padding(4, 2, 4, 2);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _path.Margin = new Padding(0);
        _jump.Margin = _copyKey.Margin = new Padding(2, 0, 2, 0);

        layout.Controls.Add(_path, 0, 0);
        layout.Controls.Add(_jump, 1, 0);
        layout.Controls.Add(_copyKey, 2, 0);

        Controls.Add(layout);

        _jump.Click += (s, e) => JumpRequested?.Invoke(this, EventArgs.Empty);
        _copyKey.Click += (s, e) => CopyKeyRequested?.Invoke(this, EventArgs.Empty);

        ApplyLocalization();
        SetEnabledState(false);
    }

    public void ApplyLocalization()
    {
        _tips.SetToolTip(_jump, Localization.T("Tooltip.JumpSelection"));
        _tips.SetToolTip(_copyKey, Localization.T("Tooltip.CopyKey"));

        if (_currentDoc != null && SelectedId >= 0)
            Set(_currentDoc, SelectedId);
    }

    public void SetEnabledState(bool enabled)
    {
        _jump.Enabled = enabled;
        _copyKey.Enabled = enabled;
    }

    public void Reset()
    {
        SelectedId = -1;
        _currentDoc = null;
        _path.Controls.Clear();
        SetEnabledState(false);
    }

    public void Set(JsonTreeDocument doc, int id)
    {
        _currentDoc = doc;
        SelectedId = id;
        _path.Controls.Clear();

        var parts = doc.Path(id);
        for (int i = 0; i < parts.Count; i++)
        {
            int pid = parts[i];
            AddSegment(doc.KeyOf(pid), pid, bold: false);
            AddSeparator();
        }
        AddSegment(doc.KeyOf(id), id, bold: true);
        SetEnabledState(true);
    }

    void AddSegment(string text, int id, bool bold)
    {
        var lbl = new LinkLabel
        {
            Text = string.IsNullOrEmpty(text) ? Localization.T("Selection.Root") : text,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        if (bold)
            lbl.Font = new Font(lbl.Font, FontStyle.Bold);
        if (!bold)
            lbl.LinkClicked += (s, e) => NodeRequested?.Invoke(this, id);
        else
            lbl.Links.Clear();
        _path.Controls.Add(lbl);
    }

    void AddSeparator()
    {
        var sep = new Label
        {
            Text = "›",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(2, 2, 2, 2),
        };
        _path.Controls.Add(sep);
    }
}
