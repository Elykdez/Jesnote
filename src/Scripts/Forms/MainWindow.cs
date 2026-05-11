using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Jasnote.Controls;
using Jasnote.Core;
using Jasnote.Forms;

namespace Jasnote.Forms;

/// <summary>
/// Main UI entrance of the application
/// </summary>
public sealed class MainWindow : Form, ILocalizable
{
    readonly AppSettings _settings;
    readonly JsonTreeDocument _doc = new();
    string? _currentFile;
    string? _pendingInitialFile;

    readonly MenuStrip _menu = new();
    readonly SearchBar _searchBar = new();
    readonly SelectionBar _selectionBar = new();
    readonly DetailPanel _detailPanel = new();
    readonly Panel _topPanel = new() { Dock = DockStyle.Top };
    readonly VirtualJsonTree _tree = new() { Dock = DockStyle.Fill };
    readonly Label _welcome =
        new()
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10.5f),
            AutoSize = false,
        };
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _elementsLabel = new() { Text = "" };
    readonly ToolStripStatusLabel _spring = new() { Spring = true };
    readonly ToolStripStatusLabel _updateLabel =
        new()
        {
            Text = "",
            IsLink = true,
            Visible = false,
        };

    // Menu items we need to enable/disable
    ToolStripMenuItem _fileNew = null!;
    ToolStripMenuItem _fileReload = null!;
    ToolStripMenuItem _fileExportFile = null!;
    ToolStripMenuItem _fileExportClipboard = null!;
    ToolStripMenuItem _fileOpenRecent = null!;
    ToolStripMenuItem _viewExpandAll = null!;
    ToolStripMenuItem _viewCollapseAll = null!;
    ToolStripMenuItem _viewShowSelection = null!;
    ToolStripMenuItem _viewShowDetail = null!;
    ToolStripMenuItem _goTop = null!;
    ToolStripMenuItem _goBottom = null!;
    ToolStripMenuItem _goSelection = null!;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        Text = AppInfo.AppName;
        ClientSize = new Size(_settings.LastWindowWidth, _settings.LastWindowHeight);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        KeyPreview = true;

        try
        {
            using var iconStream = Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream(GlobalSettings.AppIcon);
            if (iconStream != null)
                Icon = new Icon(iconStream);
        }
        catch { }

        BuildMenu();
        BuildLayout();
        ApplyLocalization();

        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;

        _tree.SelectionChanged += (s, id) => OnSelectionChanged(id);
        _searchBar.SearchRequested += (s, e) => DoSearch();
        _searchBar.ScrollTopRequested += (s, e) => _tree.ScrollToTop();
        _searchBar.ScrollBottomRequested += (s, e) => _tree.ScrollToBottom();
        _searchBar.CollapseAllRequested += (s, e) => _tree.CloseAll();
        _selectionBar.NodeRequested += (s, id) =>
        {
            _tree.SelectId(id);
            _tree.EnsureVisible(id);
        };
        _selectionBar.JumpRequested += (s, e) =>
        {
            int id = _tree.SelectedId;
            if (id >= 0)
                _tree.EnsureVisible(id);
        };
        _selectionBar.CopyKeyRequested += (s, e) =>
        {
            int id = _tree.SelectedId;
            if (id >= 0)
                CopyToClipboard(_doc.KeyOf(id));
        };
        _detailPanel.CopyValueRequested += (s, e) => CopyToClipboard(_detailPanel.RawText);

        _updateLabel.Click += (s, e) => OpenUrl(AppInfo.ReleaseUrl);

        _selectionBar.Visible = _settings.LastSelectionShown;
        _detailPanel.Visible = _settings.LastDetailShown;
        _viewShowSelection?.Checked = _selectionBar.Visible;
        _viewShowDetail?.Checked = _detailPanel.Visible;

        UpdateTopPanelHeight();

        ApplyTheme();
        SetHasDocument(false);
        RebuildRecentFilesMenu();

        FormClosing += (s, e) =>
        {
            _settings.LastWindowWidth = ClientSize.Width;
            _settings.LastWindowHeight = ClientSize.Height;
            _settings.LastSelectionShown = _selectionBar.Visible;
            _settings.LastDetailShown = _detailPanel.Visible;
            _settings.Save();
        };

        Shown += (s, e) =>
        {
            if (_settings.NotifyUpdates)
                _ = CheckUpdatesAsync();
            if (!string.IsNullOrEmpty(_pendingInitialFile))
            {
                var p = _pendingInitialFile;
                _pendingInitialFile = null;
                _ = LoadFileAsync(p);
            }
        };
    }

    // -------------------------------------------------------------------------
    // Layout / menu
    // -------------------------------------------------------------------------

    void BuildMenu()
    {
        _fileNew = MenuItem("Menu.File.New", (s, e) => DoNew());
        _fileNew.ShortcutKeys = Keys.Control | Keys.N;
        var fileOpen = MenuItem("Menu.File.Open", (s, e) => DoOpenFile());
        fileOpen.ShortcutKeys = Keys.Control | Keys.O;
        _fileOpenRecent = MenuItem("Menu.File.OpenRecent");
        var fileOpenClipboard = MenuItem(
            "Menu.File.OpenClipboard",
            (s, e) => DoOpenFromClipboard()
        );
        _fileReload = MenuItem("Menu.File.Reload", (s, e) => DoReload());
        _fileReload.ShortcutKeys = Keys.Alt | Keys.R;
        _fileExportFile = MenuItem("Menu.File.ExportFile", (s, e) => DoExportToFile());
        _fileExportClipboard = MenuItem(
            "Menu.File.ExportClipboard",
            (s, e) => DoExportToClipboard()
        );
        var fileSettings = MenuItem("Menu.File.Settings", (s, e) => DoShowSettings());
        fileSettings.ShortcutKeys = Keys.Control | Keys.Oemcomma;
        fileSettings.ShortcutKeyDisplayString = "Ctrl+,";
        var fileQuit = MenuItem("Menu.File.Quit", (s, e) => Close());
        fileQuit.ShortcutKeys = Keys.Control | Keys.Q;

        var fileMenu = MenuItem("Menu.File");
        fileMenu.DropDownItems.AddRange(
            new ToolStripItem[]
            {
                _fileNew,
                new ToolStripSeparator(),
                fileOpen,
                _fileOpenRecent,
                fileOpenClipboard,
                _fileReload,
                new ToolStripSeparator(),
                _fileExportFile,
                _fileExportClipboard,
                new ToolStripSeparator(),
                fileSettings,
                fileQuit,
            }
        );

        _viewExpandAll = MenuItem("Menu.View.ExpandAll", (s, e) => _tree.OpenAll());
        _viewCollapseAll = MenuItem("Menu.View.CollapseAll", (s, e) => _tree.CloseAll());
        _viewShowSelection = MenuItem("Menu.View.ShowSelection", (s, e) => ToggleSelectionBar());
        _viewShowSelection.CheckOnClick = false;
        _viewShowDetail = MenuItem("Menu.View.ShowDetail", (s, e) => ToggleDetailPanel());
        _viewShowDetail.CheckOnClick = false;
        var viewMenu = MenuItem("Menu.View");
        viewMenu.DropDownItems.AddRange(
            [
                _viewExpandAll,
                _viewCollapseAll,
                new ToolStripSeparator(),
                _viewShowSelection,
                _viewShowDetail,
            ]
        );

        _goTop = MenuItem("Menu.Go.Top", (s, e) => _tree.ScrollToTop());
        _goTop.ShortcutKeys = Keys.Control | Keys.Home;
        _goBottom = MenuItem("Menu.Go.Bottom", (s, e) => _tree.ScrollToBottom());
        _goBottom.ShortcutKeys = Keys.Control | Keys.End;
        _goSelection = MenuItem(
            "Menu.Go.Selection",
            (s, e) =>
            {
                int id = _selectionBar.SelectedId;
                if (id >= 0)
                    _tree.EnsureVisible(id);
            }
        );
        var goMenu = MenuItem("Menu.Go");
        goMenu.DropDownItems.AddRange([_goTop, _goBottom, _goSelection]);

        var helpReport = MenuItem("Menu.Help.ReportBug", (s, e) => OpenUrl(AppInfo.ReportUrl));
        var helpAbout = MenuItem(
            "Menu.Help.About",
            (s, e) =>
            {
                using var dlg = new AboutDialog();
                ApplyThemeToDialog(dlg);
                dlg.ShowDialog(this);
            }
        );
        var helpMenu = MenuItem("Menu.Help");
        helpMenu.DropDownItems.AddRange([helpReport, new ToolStripSeparator(), helpAbout]);

        _menu.Items.AddRange([fileMenu, viewMenu, goMenu, helpMenu]);
        MainMenuStrip = _menu;
    }

    void BuildLayout()
    {
        _status.Items.Add(_elementsLabel);
        _status.Items.Add(_spring);
        _status.Items.Add(_updateLabel);

        // Center: stack tree + welcome label
        var centerHost = new Panel { Dock = DockStyle.Fill };
        _tree.Dock = DockStyle.Fill;
        _welcome.Dock = DockStyle.Fill;
        centerHost.Controls.Add(_tree);
        centerHost.Controls.Add(_welcome);
        _welcome.BringToFront();

        // Top panel for searchbar/selection/detail stacked
        _detailPanel.Dock = DockStyle.Top;
        _selectionBar.Dock = DockStyle.Top;
        _searchBar.Dock = DockStyle.Top;
        _topPanel.Controls.Add(_detailPanel);
        _topPanel.Controls.Add(_selectionBar);
        _topPanel.Controls.Add(_searchBar);
        UpdateTopPanelHeight();

        Controls.Add(centerHost);
        Controls.Add(_topPanel);
        Controls.Add(_status);
        Controls.Add(_menu);
    }

    public void ApplyLocalization()
    {
        ApplyToolStripLocalization(_menu.Items);
        _welcome.Text = Localization.T("Welcome.Text");
        _updateLabel.Text = Localization.T("Status.UpdateAvailable");
        _searchBar.ApplyLocalization();
        _selectionBar.ApplyLocalization();
        _detailPanel.ApplyLocalization();
        UpdateElementsLabel();
    }

    void UpdateElementsLabel()
    {
        _elementsLabel.Text =
            _tree.Document == null ? "" : Localization.F("Status.Elements", _doc.Count);
    }

    static ToolStripMenuItem MenuItem(string key, EventHandler? onClick = null)
    {
        var item = new ToolStripMenuItem(Localization.T(key)) { Tag = key };
        if (onClick != null)
            item.Click += onClick;
        return item;
    }

    static void ApplyToolStripLocalization(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Tag is string key)
                item.Text = Localization.T(key);
            if (item is ToolStripDropDownItem dropDown)
                ApplyToolStripLocalization(dropDown.DropDownItems);
        }
    }

    void UpdateTopPanelHeight()
    {
        _topPanel.Height =
            _searchBar.Height
            + (_selectionBar.Visible ? _selectionBar.Height : 0)
            + (_detailPanel.Visible ? _detailPanel.Height : 0);
    }

    void ApplyTheme()
    {
        var palette = Theme.Resolve(_settings.ColorTheme);
        Theme.Apply(this, palette);
    }

    void ApplyThemeToDialog(Form f)
    {
        var palette = Theme.Resolve(_settings.ColorTheme);
        Theme.Apply(f, palette);
    }

    // -------------------------------------------------------------------------
    // Drag-drop
    // -------------------------------------------------------------------------

    void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data == null)
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;
        _ = LoadFileAsync(files[0]);
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    void DoNew()
    {
        _doc.Reset();
        _currentFile = null;
        _tree.Document = null;
        _welcome.Visible = true;
        _selectionBar.Reset();
        _detailPanel.Reset();
        UpdateElementsLabel();
        SetTitle(null);
        SetHasDocument(false);
    }

    void DoOpenFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = Localization.T("FileDialog.OpenTitle"),
            Filter = _settings.ExtensionFilter
                ? Localization.T("FileDialog.Filter.JsonOpen")
                : Localization.T("FileDialog.Filter.All"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _ = LoadFileAsync(dlg.FileName);
        }
    }

    // JSONL / NDJSON: each line is a complete JSON document. Detect by extension
    // so the loader can switch to AllowMultipleValues and wrap top-level values
    // under a synthetic array root.
    static bool IsJsonlPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ndjson", StringComparison.OrdinalIgnoreCase);
    }

    void DoOpenFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                ShowError(Localization.T("Error.ClipboardEmpty"), null);
                return;
            }
            string text = Clipboard.GetText();
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _ = LoadBytesAsync(bytes, Localization.T("Loading.ClipboardLabel"));
        }
        catch (Exception ex)
        {
            ShowError(Localization.T("Error.ClipboardReadFailed"), ex);
        }
    }

    void DoReload()
    {
        if (string.IsNullOrEmpty(_currentFile))
            return;
        _ = LoadFileAsync(_currentFile);
    }

    void DoShowSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        ApplyThemeToDialog(dlg);
        dlg.ThemeApplied = pref =>
        {
            _settings.ColorTheme = pref;
            ApplyTheme();
        };
        dlg.LanguageApplied = pref =>
        {
            _settings.Language = pref;
            ApplyLocalization();
            ApplyTheme();
        };
        dlg.ShowDialog(this);
        Localization.Apply(_settings.Language);
        ApplyLocalization();
        ApplyTheme();
        RebuildRecentFilesMenu();
    }

    void DoExportToFile()
    {
        if (_tree.SelectedId < 0)
            return;
        try
        {
            byte[] data = ExtractSelectionBytes();
            using var sfd = new SaveFileDialog
            {
                Title = Localization.T("FileDialog.ExportTitle"),
                Filter = Localization.T("FileDialog.Filter.JsonSave"),
                DefaultExt = "json",
                FileName = "export.json",
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                File.WriteAllBytes(sfd.FileName, data);
            }
        }
        catch (Exception ex)
        {
            ShowError(Localization.T("Error.ExportFailed"), ex);
        }
    }

    void DoExportToClipboard()
    {
        if (_tree.SelectedId < 0)
            return;
        try
        {
            byte[] data = ExtractSelectionBytes();
            CopyToClipboard(Encoding.UTF8.GetString(data));
        }
        catch (Exception ex)
        {
            ShowError(Localization.T("Error.ExportFailed"), ex);
        }
    }

    byte[] ExtractSelectionBytes()
    {
        int id = _tree.SelectedId;
        if (id < 0)
            throw new InvalidOperationException(Localization.T("Error.NoSelection"));
        var t = _doc.TypeOf(id);
        if (t != JsonNodeType.Array && t != JsonNodeType.Object)
            id = _doc.ParentOf(id);
        if (id < 0)
            id = JsonTreeDocument.RootId;
        return _doc.Extract(id);
    }

    void OnSelectionChanged(int id)
    {
        if (id < 0)
            return;
        _selectionBar.Set(_doc, id);
        _detailPanel.Set(_doc, id);
        _fileExportFile.Enabled = true;
        _fileExportClipboard.Enabled = true;
    }

    void ToggleSelectionBar()
    {
        _selectionBar.Visible = !_selectionBar.Visible;
        _viewShowSelection.Checked = _selectionBar.Visible;
        UpdateTopPanelHeight();
    }

    void ToggleDetailPanel()
    {
        _detailPanel.Visible = !_detailPanel.Visible;
        _viewShowDetail.Checked = _detailPanel.Visible;
        UpdateTopPanelHeight();
    }

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------

    async void DoSearch()
    {
        var pattern = _searchBar.Pattern;
        if (string.IsNullOrEmpty(pattern))
            return;
        if (_doc.Count == 0)
            return;

        var typ = _searchBar.SelectedType;
        if (typ == SearchType.Keyword)
        {
            var lower = pattern.ToLowerInvariant();
            if (lower != "true" && lower != "false" && lower != "null")
            {
                ShowError(Localization.T("Error.SearchAllowedKeywords"), null);
                return;
            }
            pattern = lower;
        }

        int start = _tree.SelectedId >= 0 ? _tree.SelectedId : JsonTreeDocument.RootId;
        using var cts = new CancellationTokenSource();
        Cursor = Cursors.WaitCursor;
        try
        {
            int found = await Task.Run(() => _doc.Search(start, pattern, typ, cts.Token));
            if (found == JsonTreeDocument.NotFound)
            {
                MessageBox.Show(
                    this,
                    Localization.F(
                        "Search.NoMatch.Message",
                        Localization.SearchTypeName(typ),
                        pattern
                    ),
                    Localization.T("Search.NoMatch.Title"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }
            _tree.SelectId(found);
        }
        catch (Exception ex)
        {
            ShowError(Localization.T("Error.SearchFailed"), ex);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------

    async Task LoadFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            ShowError(Localization.F("Error.FileNotFound", path), null);
            return;
        }
        using var dlg = new LoadingDialog { FileName = Path.GetFileName(path) };
        ApplyThemeToDialog(dlg);
        var ct = dlg.CancellationToken;
        // Show dialog first so early progress callbacks aren't dropped by the
        // IsHandleCreated guard inside the shim.
        dlg.Show(this);
        Enabled = false;
        bool jsonl = IsJsonlPath(path);
        var task = Task.Run(() => _doc.LoadAsync(path, dlg.Progress, ct, jsonl), ct);
        try
        {
            await task;
            OnDocumentLoaded(path, Path.GetFileName(path), addRecent: true);
        }
        catch (OperationCanceledException)
        { /* user cancelled */
        }
        catch (Exception ex)
        {
            ShowError(Localization.F("Error.OpenDocumentFailed", path), ex);
        }
        finally
        {
            Enabled = true;
            Activate();
            dlg.Close();
        }
    }

    async Task LoadBytesAsync(byte[] data, string label)
    {
        using var dlg = new LoadingDialog { FileName = label };
        ApplyThemeToDialog(dlg);
        var ct = dlg.CancellationToken;
        dlg.Show(this);
        Enabled = false;
        var task = Task.Run(() => _doc.LoadAsync(data, dlg.Progress, ct), ct);
        try
        {
            await task;
            OnDocumentLoaded(null, label, addRecent: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowError(Localization.F("Error.OpenDocumentFailed", label), ex);
        }
        finally
        {
            Enabled = true;
            Activate();
            dlg.Close();
        }
    }

    void OnDocumentLoaded(string? path, string label, bool addRecent)
    {
        _currentFile = path;
        _tree.Document = _doc;
        _welcome.Visible = false;
        UpdateElementsLabel();
        SetTitle(label);
        SetHasDocument(true);
        // Disable expand-all for huge docs (parity with original)
        _viewExpandAll.Enabled = _doc.Count <= 1000;
        _selectionBar.Reset();
        _detailPanel.Reset();
        if (addRecent && !string.IsNullOrEmpty(path))
        {
            _settings.PushRecentFile(path);
            _settings.Save();
            RebuildRecentFilesMenu();
        }
    }

    void SetTitle(string? fileName)
    {
        Text = string.IsNullOrEmpty(fileName) ? "Jasnote" : $"{fileName} - Jasnote";
    }

    void SetHasDocument(bool hasDoc)
    {
        _searchBar.SetEnabledState(hasDoc);
        _fileNew.Enabled = hasDoc;
        _fileReload.Enabled = hasDoc && !string.IsNullOrEmpty(_currentFile);
        _fileExportFile.Enabled = hasDoc && _tree.SelectedId >= 0;
        _fileExportClipboard.Enabled = hasDoc;
        _viewExpandAll.Enabled = hasDoc;
        _viewCollapseAll.Enabled = hasDoc;
        _goTop.Enabled = hasDoc;
        _goBottom.Enabled = hasDoc;
        _goSelection.Enabled = hasDoc;
    }

    // -------------------------------------------------------------------------
    // Recent files
    // -------------------------------------------------------------------------

    void RebuildRecentFilesMenu()
    {
        _fileOpenRecent.DropDownItems.Clear();
        var files = _settings.RecentFiles;
        if (files.Count == 0)
        {
            _fileOpenRecent.Enabled = false;
            return;
        }
        _fileOpenRecent.Enabled = true;
        foreach (var f in files)
        {
            var item = new ToolStripMenuItem(f);
            string capture = f;
            item.Click += (s, e) => _ = LoadFileAsync(capture);
            _fileOpenRecent.DropDownItems.Add(item);
        }
    }

    // -------------------------------------------------------------------------
    // Misc helpers
    // -------------------------------------------------------------------------

    void ShowError(string message, Exception? ex)
    {
        MessageBox.Show(
            this,
            ex == null ? message : $"{message}\r\n\r\n{ex.Message}",
            Localization.T("Common.Error"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );
    }

    static void CopyToClipboard(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text))
                Clipboard.Clear();
            else
                Clipboard.SetText(text);
        }
        catch
        { /* clipboard contention is non-fatal */
        }
    }

    static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    async Task CheckUpdatesAsync()
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var (_, isNewer) = await GithubUpdater
            .CheckAsync(current, CancellationToken.None)
            .ConfigureAwait(false);
        if (!isNewer)
            return;
        if (IsDisposed)
            return;
        BeginInvoke(new Action(() => _updateLabel.Visible = true));
    }

    // -------------------------------------------------------------------------
    // Command-line file argument
    // -------------------------------------------------------------------------

    public void LoadInitial(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _pendingInitialFile = path;
    }
}
