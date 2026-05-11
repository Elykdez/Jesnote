using System.Drawing;
using System.Windows.Forms;

namespace Jasnote.Forms;

public sealed class SettingsDialog : Form, ILocalizable
{
    static readonly ColorThemePreference[] s_themeOrder =
    [
        ColorThemePreference.Auto,
        ColorThemePreference.Light,
        ColorThemePreference.Dark,
    ];

    static readonly LanguagePreference[] s_languageOrder =
    [
        LanguagePreference.Auto,
        LanguagePreference.English,
        LanguagePreference.ChineseSimplified,
    ];

    readonly AppSettings _settings;
    readonly NumericUpDown _recentCount =
        new()
        {
            Minimum = 3,
            Maximum = 20,
            Width = 80,
        };
    readonly CheckBox _extFilter = new() { AutoSize = true };
    readonly CheckBox _notifyUpdates = new() { AutoSize = true };
    readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly Label _recentCountLabel = NewLabel();
    readonly Label _filterLabel = NewLabel();
    readonly Label _updatesLabel = NewLabel();
    readonly Label _appearanceLabel = NewLabel();
    readonly Label _languageLabel = NewLabel();
    readonly Button _close = new() { DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };

    bool _updatingChoices;

    public Action<ColorThemePreference>? ThemeApplied;
    public Action<LanguagePreference>? LanguageApplied;

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 280);

        _recentCount.Value = Math.Clamp(
            _settings.RecentFileCount,
            (int)_recentCount.Minimum,
            (int)_recentCount.Maximum
        );
        _extFilter.Checked = _settings.ExtensionFilter;
        _notifyUpdates.Checked = _settings.NotifyUpdates;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(14),
            AutoSize = false,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(panel, _recentCountLabel, _recentCount);
        AddRow(panel, _filterLabel, _extFilter);
        AddRow(panel, _updatesLabel, _notifyUpdates);
        AddRow(panel, _appearanceLabel, _theme);
        AddRow(panel, _languageLabel, _language);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8),
        };
        btnRow.Controls.Add(_close);
        AcceptButton = _close;

        Controls.Add(panel);
        Controls.Add(btnRow);

        ApplyLocalization();

        _recentCount.ValueChanged += (s, e) => _settings.RecentFileCount = (int)_recentCount.Value;
        _extFilter.CheckedChanged += (s, e) => _settings.ExtensionFilter = _extFilter.Checked;
        _notifyUpdates.CheckedChanged += (s, e) => _settings.NotifyUpdates = _notifyUpdates.Checked;
        _theme.SelectedIndexChanged += (s, e) =>
        {
            if (_updatingChoices || _theme.SelectedItem is not Choice<ColorThemePreference> choice)
                return;

            _settings.ColorTheme = choice.Value;
            ThemeApplied?.Invoke(_settings.ColorTheme);
        };
        _language.SelectedIndexChanged += (s, e) =>
        {
            if (_updatingChoices || _language.SelectedItem is not Choice<LanguagePreference> choice)
                return;

            _settings.Language = choice.Value;
            Localization.Apply(_settings.Language);
            ApplyLocalization();
            LanguageApplied?.Invoke(_settings.Language);
        };

        FormClosed += (s, e) => _settings.Save();
    }

    public void ApplyLocalization()
    {
        Text = Localization.T("Settings.Title");
        _recentCountLabel.Text = Localization.T("Settings.RecentFiles");
        _filterLabel.Text = Localization.T("Settings.FileFilter");
        _updatesLabel.Text = Localization.T("Settings.Updates");
        _appearanceLabel.Text = Localization.T("Settings.Appearance");
        _languageLabel.Text = Localization.T("Settings.Language");
        _extFilter.Text = Localization.T("Settings.ExtensionFilter");
        _notifyUpdates.Text = Localization.T("Settings.NotifyUpdates");
        _close.Text = Localization.T("Common.Close");

        _updatingChoices = true;
        try
        {
            PopulateChoices(_theme, s_themeOrder, _settings.ColorTheme, Localization.ThemeName);
            PopulateChoices(
                _language,
                s_languageOrder,
                _settings.Language,
                Localization.LanguageName
            );
        }
        finally
        {
            _updatingChoices = false;
        }
    }

    static void PopulateChoices<T>(
        ComboBox combo,
        IReadOnlyList<T> values,
        T selected,
        Func<T, string> textFactory
    )
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        var selectedIndex = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            combo.Items.Add(new Choice<T>(value, textFactory(value)));
            if (EqualityComparer<T>.Default.Equals(value, selected))
                selectedIndex = i;
        }
        combo.SelectedIndex = selectedIndex;
        combo.EndUpdate();
    }

    static void AddRow(TableLayoutPanel panel, Label label, Control widget)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        widget.Margin = new Padding(0, 3, 0, 3);
        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(widget, 1, row);
    }

    static Label NewLabel() => new() { AutoSize = true, Margin = new Padding(0, 6, 12, 6) };

    sealed class Choice<T>(T value, string text)
    {
        public T Value { get; } = value;
        readonly string _text = text;

        public override string ToString() => _text;
    }
}
