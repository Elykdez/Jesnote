using System.Drawing;
using System.Windows.Forms;
using Jasnote.Core;

namespace Jasnote.Controls;

public sealed class DetailPanel : UserControl, ILocalizable
{
    readonly TextBox _value =
        new()
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5f),
        };
    readonly Button _copy =
        new()
        {
            Text = "⧉",
            Width = 32,
            Dock = DockStyle.Right,
        };
    readonly ToolTip _tips = new();
    JsonTreeDocument? _currentDoc;
    int _currentId = -1;

    public event EventHandler? CopyValueRequested;

    public string RawText { get; private set; } = "";

    public DetailPanel()
    {
        Height = 80;
        Dock = DockStyle.Top;
        Padding = new Padding(4, 2, 4, 4);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(_value, 0, 0);
        layout.Controls.Add(_copy, 1, 0);
        Controls.Add(layout);

        _copy.Click += (s, e) => CopyValueRequested?.Invoke(this, EventArgs.Empty);
        ApplyLocalization();
        SetEnabledState(false);
    }

    public void ApplyLocalization()
    {
        _tips.SetToolTip(_copy, Localization.T("Tooltip.CopyValue"));
        if (_currentDoc != null && _currentId >= 0)
            Render(_currentDoc, _currentId);
    }

    public void SetEnabledState(bool enabled) => _copy.Enabled = enabled;

    public void Reset()
    {
        _currentDoc = null;
        _currentId = -1;
        RawText = "";
        _value.Text = "";
        SetEnabledState(false);
    }

    // WinForms TextBox.Text becomes unresponsive past ~1 MB — for huge string
    // values we cap what's shown but keep the full payload in RawText for copy.
    const int DetailStringCap = 64 * 1024;

    public void Set(JsonTreeDocument doc, int id)
    {
        _currentDoc = doc;
        _currentId = id;
        Render(doc, id);
    }

    void Render(JsonTreeDocument doc, int id)
    {
        if (doc.IsBranch(id))
        {
            int count = doc.ChildCount(id);
            var type = doc.TypeOf(id);
            string shown = type == JsonNodeType.Array ? "[...]" : "{...}";
            _value.Text =
                $"{Localization.F("Detail.BranchSummary", Localization.JsonNodeTypeName(type), count)}\r\n\r\n{shown}";
            RawText = "";
            SetEnabledState(false);
        }
        else
        {
            string display = doc.DisplayValueCapped(id, DetailStringCap);
            string raw = doc.RawValue(id);
            _value.Text = $"{Localization.JsonNodeTypeName(doc.TypeOf(id))}\r\n\r\n{display}";
            RawText = raw;
            SetEnabledState(true);
        }
    }
}
