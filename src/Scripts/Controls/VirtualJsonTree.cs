using System.ComponentModel;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using Jasnote.Core;

namespace Jasnote.Controls;

/// <summary>
/// Owner-drawn, virtualized tree view for <see cref="JsonTreeDocument"/>.
/// Only the rows visible on screen are painted; toggling a branch splices
/// descendants in or out of the flat visible-rows list — <c>O(visible delta)</c>,
/// not a full re-walk of the document. Designed for millions of nodes — never
/// realizes a TreeNode per JSON node.
/// </summary>
public sealed class VirtualJsonTree : Control
{
    JsonTreeDocument? _doc;
    readonly HashSet<int> _openBranches = new();
    readonly List<int> _visible = new(); // flat list of visible node ids
    readonly List<int> _depths = new(); // matched depth per visible row
    int _firstVisibleRow; // for vertical scrolling
    int _hScroll; // pixels scrolled horizontally
    int _selectedRow = -1;
    int _rowHeight = 20;
    int _indentWidth = 18;
    int _wheelRemainder; // accumulator for high-resolution wheel deltas

    readonly VScrollBar _vScroll = new() { Dock = DockStyle.Right };
    readonly HScrollBar _hScrollBar = new() { Dock = DockStyle.Bottom };

    readonly StringFormat _keyFormat =
        new(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

    // Cached GDI+ objects — recreated lazily when a color changes. Previously
    // every paint allocated ~3 brushes and 1 pen per row, churning hundreds of
    // GDI handles per second while scrolling.
    readonly Dictionary<int, SolidBrush> _brushCache = new();
    readonly Dictionary<int, Pen> _penCache = new();

    Color _keyColor = SystemColors.ControlText;
    Color _containerColor = SystemColors.ControlText;
    Color _stringColor = Color.FromArgb(176, 121, 0);
    Color _numberColor = Color.FromArgb(0, 128, 0);
    Color _boolNullColor = Color.FromArgb(192, 32, 32);
    Color _guideColor = Color.FromArgb(180, 180, 180);
    Color _selectedBack = SystemColors.Highlight;
    Color _selectedFore = SystemColors.HighlightText;

    public Color KeyColor
    {
        get => _keyColor;
        set
        {
            _keyColor = value;
            Invalidate();
        }
    }
    public Color ContainerColor
    {
        get => _containerColor;
        set
        {
            _containerColor = value;
            Invalidate();
        }
    }
    public Color StringColor
    {
        get => _stringColor;
        set
        {
            _stringColor = value;
            Invalidate();
        }
    }
    public Color NumberColor
    {
        get => _numberColor;
        set
        {
            _numberColor = value;
            Invalidate();
        }
    }
    public Color BoolNullColor
    {
        get => _boolNullColor;
        set
        {
            _boolNullColor = value;
            Invalidate();
        }
    }
    public Color GuideColor
    {
        get => _guideColor;
        set
        {
            _guideColor = value;
            Invalidate();
        }
    }
    public Color SelectedBack
    {
        get => _selectedBack;
        set
        {
            _selectedBack = value;
            Invalidate();
        }
    }
    public Color SelectedFore
    {
        get => _selectedFore;
        set
        {
            _selectedFore = value;
            Invalidate();
        }
    }

    public event EventHandler<int>? SelectionChanged;

    public VirtualJsonTree()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable,
            true
        );
        DoubleBuffered = true;
        TabStop = true;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        Font = new Font("Segoe UI", 9.0f);

        Controls.Add(_hScrollBar);
        Controls.Add(_vScroll);
        _vScroll.Scroll += (s, e) =>
        {
            _firstVisibleRow = _vScroll.Value;
            Invalidate();
        };
        _hScrollBar.Scroll += (s, e) =>
        {
            _hScroll = _hScrollBar.Value;
            Invalidate();
        };
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public JsonTreeDocument? Document
    {
        get => _doc;
        set
        {
            _doc = value;
            _openBranches.Clear();
            _visible.Clear();
            _depths.Clear();
            _selectedRow = -1;
            _firstVisibleRow = 0;
            _hScroll = 0;
            if (_doc != null && _doc.Count > 0)
            {
                _openBranches.Add(JsonTreeDocument.RootId);
                RebuildVisible();
            }
            UpdateScrollBars();
            Invalidate();
        }
    }

    public int SelectedId =>
        (_selectedRow >= 0 && _selectedRow < _visible.Count) ? _visible[_selectedRow] : -1;

    public bool IsBranchOpen(int id) => _openBranches.Contains(id);

    public void OpenBranch(int id)
    {
        if (_doc == null || !_doc.IsBranch(id) || !_openBranches.Add(id))
            return;
        int row = _visible.IndexOf(id);
        if (row >= 0)
        {
            SpliceInChildren(id, row);
        }
        // else: branch isn't currently visible (an ancestor is closed); when
        // that ancestor opens, expansion will pick up the open state.
        UpdateScrollBars();
        Invalidate();
    }

    public void CloseBranch(int id)
    {
        if (_doc == null || !_openBranches.Remove(id))
            return;
        int row = _visible.IndexOf(id);
        if (row >= 0)
        {
            SpliceOutDescendants(row);
        }
        UpdateScrollBars();
        Invalidate();
    }

    public void ToggleBranch(int id)
    {
        if (_openBranches.Contains(id))
            CloseBranch(id);
        else
            OpenBranch(id);
    }

    public void OpenAll()
    {
        if (_doc == null)
            return;
        _openBranches.Clear();
        for (int i = 0; i < _doc.Count; i++)
            if (_doc.IsBranch(i))
                _openBranches.Add(i);
        RebuildVisible();
        UpdateScrollBars();
        Invalidate();
    }

    public void CloseAll()
    {
        if (_doc == null)
            return;
        _openBranches.Clear();
        _openBranches.Add(JsonTreeDocument.RootId);
        RebuildVisible();
        UpdateScrollBars();
        _firstVisibleRow = 0;
        Invalidate();
    }

    public void ScrollToTop()
    {
        _firstVisibleRow = 0;
        if (_vScroll.Enabled)
            _vScroll.Value = 0;
        Invalidate();
    }

    public void ScrollToBottom()
    {
        int max = Math.Max(0, _visible.Count - VisibleRowCount);
        _firstVisibleRow = max;
        if (_vScroll.Enabled)
            _vScroll.Value = Math.Min(_vScroll.Maximum, max);
        Invalidate();
    }

    public void EnsureVisible(int id)
    {
        if (_doc == null || id < 0 || id >= _doc.Count)
            return;
        // Open ancestors top-down so each splice is incremental.
        foreach (int p in _doc.Path(id))
        {
            if (!_openBranches.Contains(p))
                OpenBranch(p);
        }
        int row = _visible.IndexOf(id);
        if (row < 0)
            return;
        if (row < _firstVisibleRow)
            _firstVisibleRow = row;
        else if (row >= _firstVisibleRow + VisibleRowCount)
            _firstVisibleRow = Math.Max(0, row - VisibleRowCount + 1);
        UpdateScrollBars();
        Invalidate();
    }

    public void SelectId(int id)
    {
        if (_doc == null)
            return;
        EnsureVisible(id);
        int row = _visible.IndexOf(id);
        if (row < 0)
            return;
        _selectedRow = row;
        SelectionChanged?.Invoke(this, id);
        Invalidate();
    }

    // -------------------------------------------------------------------------
    // Visible-row materialization
    // -------------------------------------------------------------------------

    int VisibleRowCount => Math.Max(1, (ClientSize.Height - _hScrollBar.Height) / _rowHeight);

    void RebuildVisible()
    {
        _visible.Clear();
        _depths.Clear();
        if (_doc == null || _doc.Count == 0)
            return;

        // Root is hidden — its children appear at depth 0. Iterative DFS to
        // avoid stack-overflow on pathologically deep trees.
        var stack = new Stack<(int Id, int Depth)>();
        PushChildrenReversed(JsonTreeDocument.RootId, 0, stack);

        while (stack.Count > 0)
        {
            var (id, depth) = stack.Pop();
            _visible.Add(id);
            _depths.Add(depth);

            if (_doc.IsBranch(id) && _openBranches.Contains(id))
            {
                PushChildrenReversed(id, depth + 1, stack);
            }
        }
    }

    /// <summary>
    /// Splices the (recursive) expansion of the open subtree rooted at <paramref name="branchId"/>
    /// into the visible list immediately after row <paramref name="branchRow"/>.
    /// </summary>
    void SpliceInChildren(int branchId, int branchRow)
    {
        if (_doc == null)
            return;
        int depth = _depths[branchRow];
        var ids = new List<int>();
        var depths = new List<int>();

        // Iterative DFS, respecting which descendants are themselves open.
        var stack = new Stack<(int Id, int Depth)>();
        PushChildrenReversed(branchId, depth + 1, stack);
        while (stack.Count > 0)
        {
            var (id, d) = stack.Pop();
            ids.Add(id);
            depths.Add(d);
            if (_doc.IsBranch(id) && _openBranches.Contains(id))
            {
                PushChildrenReversed(id, d + 1, stack);
            }
        }

        if (ids.Count > 0)
        {
            _visible.InsertRange(branchRow + 1, ids);
            _depths.InsertRange(branchRow + 1, depths);
        }
    }

    /// <summary>
    /// Removes the contiguous block of descendant rows that follow
    /// <paramref name="branchRow"/>. Relies on the DFS-preorder invariant:
    /// descendants of a row are exactly the subsequent rows whose depth is greater.
    /// </summary>
    void SpliceOutDescendants(int branchRow)
    {
        int depth = _depths[branchRow];
        int end = branchRow + 1;
        while (end < _visible.Count && _depths[end] > depth)
            end++;
        int n = end - (branchRow + 1);
        if (n > 0)
        {
            _visible.RemoveRange(branchRow + 1, n);
            _depths.RemoveRange(branchRow + 1, n);
        }
        // Also adjust selection / scroll if they fell into the removed region.
        if (_selectedRow > branchRow && _selectedRow < end)
        {
            _selectedRow = branchRow;
        }
        if (_firstVisibleRow >= _visible.Count)
        {
            _firstVisibleRow = Math.Max(0, _visible.Count - VisibleRowCount);
        }
    }

    void PushChildrenReversed(int parentId, int depth, Stack<(int Id, int Depth)> stack)
    {
        // Walk to the last child, then push in reverse so DFS pops left-to-right.
        // We push without an intermediate list to avoid an allocation per parent.
        int c = _doc!.FirstChild[parentId];
        if (c == -1)
            return;
        // Find last child
        int count = 0;
        int cur = c;
        while (cur != -1)
        {
            count++;
            cur = _doc.NextSibling[cur];
        }
        // Buffer locally — small for typical fanout
        Span<int> kids = count <= 64 ? stackalloc int[count] : new int[count];
        cur = c;
        for (int i = 0; i < count; i++)
        {
            kids[i] = cur;
            cur = _doc.NextSibling[cur];
        }
        for (int i = count - 1; i >= 0; i--)
            stack.Push((kids[i], depth));
    }

    void UpdateScrollBars()
    {
        int rows = _visible.Count;
        int visibleRows = VisibleRowCount;
        if (rows <= visibleRows)
        {
            _vScroll.Enabled = false;
            _vScroll.Maximum = 0;
            _vScroll.LargeChange = 1;
            _vScroll.Value = 0;
            _firstVisibleRow = 0;
        }
        else
        {
            _vScroll.Enabled = true;
            _vScroll.LargeChange = Math.Max(1, visibleRows);
            _vScroll.SmallChange = 1;
            // Maximum is set so that (Maximum - LargeChange + 1) is the last reachable Value.
            _vScroll.Maximum = rows - 1;
            if (_firstVisibleRow > rows - visibleRows)
                _firstVisibleRow = Math.Max(0, rows - visibleRows);
            _vScroll.Value = Math.Min(_vScroll.Maximum, _firstVisibleRow);
        }

        // Horizontal scroll bar — rough estimate based on widest visible row
        int widest = EstimateWidestVisible();
        if (widest <= ClientSize.Width - _vScroll.Width)
        {
            _hScrollBar.Enabled = false;
            _hScrollBar.Maximum = 0;
            _hScrollBar.Value = 0;
            _hScroll = 0;
        }
        else
        {
            _hScrollBar.Enabled = true;
            _hScrollBar.LargeChange = Math.Max(1, ClientSize.Width - _vScroll.Width);
            _hScrollBar.SmallChange = _indentWidth;
            _hScrollBar.Maximum = widest;
            if (_hScroll > widest)
                _hScroll = widest;
            _hScrollBar.Value = Math.Min(_hScrollBar.Maximum, _hScroll);
        }
    }

    int EstimateWidestVisible()
    {
        int maxDepth = 0;
        int top = _firstVisibleRow;
        int last = Math.Min(_visible.Count, _firstVisibleRow + VisibleRowCount);
        for (int i = top; i < last; i++)
            if (_depths[i] > maxDepth)
                maxDepth = _depths[i];
        return (maxDepth + 4) * _indentWidth + 800;
    }

    static string TreeRowPreview(string text)
    {
        if (text.IndexOfAny(['\r', '\n']) < 0)
            return text;

        return string.Create(
            text.Length,
            text,
            static (buffer, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    buffer[i] = c is '\r' or '\n' ? ' ' : c;
                }
            }
        );
    }

    // -------------------------------------------------------------------------
    // Painting
    // -------------------------------------------------------------------------

    SolidBrush BrushFor(Color c)
    {
        int key = c.ToArgb();
        if (!_brushCache.TryGetValue(key, out var b))
        {
            b = new SolidBrush(c);
            _brushCache[key] = b;
        }
        return b;
    }

    Pen PenFor(Color c)
    {
        int key = c.ToArgb();
        if (!_penCache.TryGetValue(key, out var p))
        {
            p = new Pen(c);
            _penCache[key] = p;
        }
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);
        if (_doc == null || _visible.Count == 0)
            return;

        int rowsToDraw = VisibleRowCount;
        int last = Math.Min(_visible.Count, _firstVisibleRow + rowsToDraw + 1);
        int contentLeft = -_hScroll;
        int contentRight = ClientSize.Width - _vScroll.Width;

        var selectedBackBrush = BrushFor(_selectedBack);
        var selectedForeBrush = BrushFor(_selectedFore);
        var keyBrush = BrushFor(_keyColor);
        var guidePen = PenFor(_guideColor);

        for (int row = _firstVisibleRow; row < last; row++)
        {
            int y = (row - _firstVisibleRow) * _rowHeight;
            int id = _visible[row];
            int depth = _depths[row];
            bool selected = row == _selectedRow;

            if (selected)
            {
                g.FillRectangle(selectedBackBrush, 0, y, contentRight, _rowHeight);
            }

            int x = contentLeft + 2;
            for (int d = 0; d < depth; d++)
            {
                int gx = x + d * _indentWidth + _indentWidth / 2;
                g.DrawLine(guidePen, gx, y, gx, y + _rowHeight);
            }

            int glyphX = x + depth * _indentWidth;
            bool isBranch = _doc.IsBranch(id);
            if (isBranch)
            {
                bool isOpen = _openBranches.Contains(id);
                DrawGlyph(g, glyphX + 2, y + (_rowHeight - 10) / 2, isOpen, selected);
            }

            int textX = glyphX + _indentWidth;
            int textY = y + 2;

            // TODO: Investigate performance cost
            // string key = TreeRowPreview(_doc.KeyOf(id));
            string key = _doc.KeyOf(id);
            string keyText = key.Length == 0 ? string.Empty : key + " :";
            var kb = selected ? selectedForeBrush : keyBrush;
            g.DrawString(keyText, Font, kb, textX, textY, _keyFormat);
            SizeF keySize = g.MeasureString(keyText, Font, int.MaxValue, _keyFormat);
            int valueX = textX + (int)Math.Ceiling(keySize.Width) + 6;

            // TODO: Investigate performance cost
            // string valueText = TreeRowPreview(ValueDisplay(id));
            string valueText = ValueDisplay(id);
            Color valueColor = ValueColor(id, selected);
            var vBrush = selected ? selectedForeBrush : BrushFor(valueColor);
            var rect = new RectangleF(valueX, textY, contentRight - valueX, _rowHeight);
            g.DrawString(valueText, Font, vBrush, rect, _keyFormat);
        }
    }

    static void DrawGlyph(Graphics g, int x, int y, bool open, bool selected)
    {
        // Triangle expand/collapse indicator: ▶ closed, ▼ open
        var color = selected ? SystemColors.HighlightText : SystemColors.ControlText;
        using var brush = new SolidBrush(color);
        var pts = open
            ? new[] { new Point(x, y + 2), new Point(x + 9, y + 2), new Point(x + 4, y + 8) }
            : new[] { new Point(x + 2, y), new Point(x + 8, y + 5), new Point(x + 2, y + 10) };
        g.FillPolygon(brush, pts);
    }

    // Cap per-row string display so a single multi-megabyte string value can't
    // freeze the paint loop (GDI+ DrawString degrades badly past ~10 KB).
    const int RowStringCap = 200;

    string ValueDisplay(int id)
    {
        var t = _doc!.TypeOf(id);
        if (t == JsonNodeType.Object)
        {
            if (_openBranches.Contains(id))
                return _doc.FirstChild[id] == -1 ? "{}" : string.Empty;
            return _doc.FirstChild[id] == -1 ? "{}" : "{...}";
        }
        if (t == JsonNodeType.Array)
        {
            if (_openBranches.Contains(id))
                return _doc.FirstChild[id] == -1 ? "[]" : string.Empty;
            return _doc.FirstChild[id] == -1 ? "[]" : "[...]";
        }
        return _doc.DisplayValueCapped(id, RowStringCap);
    }

    Color ValueColor(int id, bool selected)
    {
        if (selected)
            return _selectedFore;
        return _doc!.TypeOf(id) switch
        {
            JsonNodeType.Array or JsonNodeType.Object => _containerColor,
            JsonNodeType.String => _stringColor,
            JsonNodeType.Number => _numberColor,
            JsonNodeType.Boolean or JsonNodeType.Null => _boolNullColor,
            _ => ForeColor,
        };
    }

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateScrollBars();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // High-resolution precision wheels send deltas smaller than the
        // SystemInformation.MouseWheelScrollDelta (120). Accumulate so they
        // aren't silently truncated to zero.
        int total = _wheelRemainder + -e.Delta * SystemInformation.MouseWheelScrollLines;
        int denom = SystemInformation.MouseWheelScrollDelta;
        int lines = total / denom;
        _wheelRemainder = total - lines * denom;

        int max = Math.Max(0, _visible.Count - VisibleRowCount);
        _firstVisibleRow = Math.Clamp(_firstVisibleRow + lines, 0, max);
        if (_vScroll.Enabled)
            _vScroll.Value = Math.Min(_vScroll.Maximum, _firstVisibleRow);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_doc == null || _visible.Count == 0)
            return;

        int row = _firstVisibleRow + e.Y / _rowHeight;
        if (row < 0 || row >= _visible.Count)
            return;

        int id = _visible[row];
        int depth = _depths[row];
        int glyphX = -_hScroll + 2 + depth * _indentWidth;

        if (_doc.IsBranch(id) && e.X >= glyphX && e.X <= glyphX + _indentWidth)
        {
            ToggleBranch(id);
            return;
        }

        _selectedRow = row;
        SelectionChanged?.Invoke(this, id);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_doc == null)
            return;
        int row = _firstVisibleRow + e.Y / _rowHeight;
        if (row < 0 || row >= _visible.Count)
            return;
        int id = _visible[row];
        if (_doc.IsBranch(id))
            ToggleBranch(id);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_doc == null || _visible.Count == 0)
            return;

        int max = _visible.Count - 1;
        int prev = _selectedRow;
        switch (e.KeyCode)
        {
            case Keys.Up:
                _selectedRow = Math.Max(0, _selectedRow - 1);
                break;
            case Keys.Down:
                _selectedRow = _selectedRow < 0 ? 0 : Math.Min(max, _selectedRow + 1);
                break;
            case Keys.PageUp:
                _selectedRow = Math.Max(0, _selectedRow - VisibleRowCount);
                break;
            case Keys.PageDown:
                _selectedRow =
                    _selectedRow < 0
                        ? VisibleRowCount - 1
                        : Math.Min(max, _selectedRow + VisibleRowCount);
                break;
            case Keys.Home:
                _selectedRow = 0;
                break;
            case Keys.End:
                _selectedRow = max;
                break;
            case Keys.Right:
                if (_selectedRow >= 0)
                {
                    int id = _visible[_selectedRow];
                    if (_doc.IsBranch(id) && !_openBranches.Contains(id))
                        OpenBranch(id);
                }
                break;
            case Keys.Left:
                if (_selectedRow >= 0)
                {
                    int id = _visible[_selectedRow];
                    if (_doc.IsBranch(id) && _openBranches.Contains(id))
                        CloseBranch(id);
                    else
                    {
                        int parent = _doc.ParentOf(id);
                        if (parent > 0)
                        {
                            int row = _visible.IndexOf(parent);
                            if (row >= 0)
                                _selectedRow = row;
                        }
                    }
                }
                break;
            default:
                return;
        }
        if (_selectedRow != prev && _selectedRow >= 0)
        {
            if (_selectedRow < _firstVisibleRow)
                _firstVisibleRow = _selectedRow;
            else if (_selectedRow >= _firstVisibleRow + VisibleRowCount)
                _firstVisibleRow = _selectedRow - VisibleRowCount + 1;
            if (_vScroll.Enabled)
                _vScroll.Value = Math.Min(_vScroll.Maximum, _firstVisibleRow);
            SelectionChanged?.Invoke(this, _visible[_selectedRow]);
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keyFormat.Dispose();
            foreach (var b in _brushCache.Values)
                b.Dispose();
            _brushCache.Clear();
            foreach (var p in _penCache.Values)
                p.Dispose();
            _penCache.Clear();
        }
        base.Dispose(disposing);
    }
}
