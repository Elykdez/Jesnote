using System.Drawing;
using System.Windows.Forms;
using Jasnote.Core;

namespace Jasnote.Controls;

public sealed class SearchBar : UserControl, ILocalizable
{
    readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 };
    readonly TextBox _entry = new();
    readonly Button _search = new() { Width = 80 };
    readonly Button _top = new() { Text = "↑", Width = 32 };
    readonly Button _bottom = new() { Text = "↓", Width = 32 };
    readonly Button _collapse = new() { Text = "-", Width = 32 };
    readonly ToolTip _tips = new();

    public event EventHandler? SearchRequested;
    public event EventHandler? ScrollTopRequested;
    public event EventHandler? ScrollBottomRequested;
    public event EventHandler? CollapseAllRequested;

    public SearchType SelectedType =>
        _type.SelectedItem switch
        {
            SearchTypeChoice choice => choice.Type,
            _ => SearchType.Key,
        };
    public string Pattern => _entry.Text;

    public SearchBar()
    {
        Height = 36;
        Dock = DockStyle.Top;
        Padding = new Padding(4);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _entry.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _entry.Margin = new Padding(4, 2, 4, 2);
        _type.Margin = new Padding(0, 2, 4, 2);
        _search.Margin = _top.Margin = _bottom.Margin = _collapse.Margin = new Padding(2);

        layout.Controls.Add(_type, 0, 0);
        layout.Controls.Add(_entry, 1, 0);
        layout.Controls.Add(_search, 2, 0);
        layout.Controls.Add(_top, 3, 0);
        layout.Controls.Add(_bottom, 4, 0);
        layout.Controls.Add(_collapse, 5, 0);
        Controls.Add(layout);

        _search.Click += (s, e) => SearchRequested?.Invoke(this, EventArgs.Empty);
        _entry.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SearchRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _top.Click += (s, e) => ScrollTopRequested?.Invoke(this, EventArgs.Empty);
        _bottom.Click += (s, e) => ScrollBottomRequested?.Invoke(this, EventArgs.Empty);
        _collapse.Click += (s, e) => CollapseAllRequested?.Invoke(this, EventArgs.Empty);

        ApplyLocalization();
        SetEnabledState(false);
    }

    public void ApplyLocalization()
    {
        var selected = SelectedType;

        _entry.PlaceholderText = Localization.T("Search.Placeholder");
        _search.Text = Localization.T("Search.Button");
        _tips.SetToolTip(_type, Localization.T("Tooltip.SearchType"));
        _tips.SetToolTip(_search, Localization.T("Tooltip.Search"));
        _tips.SetToolTip(_top, Localization.T("Tooltip.ScrollTop"));
        _tips.SetToolTip(_bottom, Localization.T("Tooltip.ScrollBottom"));
        _tips.SetToolTip(_collapse, Localization.T("Tooltip.CollapseAll"));

        _type.BeginUpdate();
        _type.Items.Clear();
        var choices = new[]
        {
            SearchType.Key,
            SearchType.Keyword,
            SearchType.Number,
            SearchType.String,
        };
        foreach (var type in choices)
            _type.Items.Add(new SearchTypeChoice(type));
        _type.SelectedIndex = Math.Max(0, Array.IndexOf(choices, selected));
        _type.EndUpdate();
    }

    public void SetEnabledState(bool enabled)
    {
        _type.Enabled = enabled;
        _entry.Enabled = enabled;
        _search.Enabled = enabled;
        _top.Enabled = enabled;
        _bottom.Enabled = enabled;
        _collapse.Enabled = enabled;
    }

    public void FocusEntry() => _entry.Focus();

    sealed class SearchTypeChoice(SearchType type)
    {
        public SearchType Type { get; } = type;

        public override string ToString() => Localization.SearchTypeName(Type);
    }
}
