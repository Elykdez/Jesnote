using System.Drawing;
using System.Windows.Forms;
using Jasnote.Core;

namespace Jasnote.Forms;

public sealed class LoadingDialog : Form, ILocalizable
{
    const int ProgressBarHeight = 14;

    readonly Label _info =
        new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
    readonly ProgressBar _indeterminate =
        new()
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Dock = DockStyle.Fill,
        };
    readonly ProgressBar _determinate =
        new()
        {
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 10_000,
            Dock = DockStyle.Fill,
            Visible = false,
        };
    readonly Button _cancel = new() { AutoSize = true };
    readonly Panel _progressPanel =
        new()
        {
            Dock = DockStyle.Top,
            Height = ProgressBarHeight,
            Margin = new Padding(0, 8, 0, 0),
        };
    readonly CancellationTokenSource _cts = new();

    public CancellationToken CancellationToken => _cts.Token;
    public string FileName { get; set; } = "";

    public LoadingDialog()
    {
        Text = Localization.T("Loading.Title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 110);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_info, 0, 0);
        layout.SetColumnSpan(_info, 2);

        _progressPanel.Controls.Add(_indeterminate);
        _progressPanel.Controls.Add(_determinate);
        layout.Controls.Add(_progressPanel, 0, 1);

        _cancel.Click += (s, e) => Cancel();
        layout.Controls.Add(_cancel, 1, 1);

        Controls.Add(layout);
        ApplyLocalization();

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Cancel();
        };
        FormClosing += (s, e) => Cancel();
    }

    public void Cancel()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
    }

    public void ApplyLocalization()
    {
        Text = Localization.T("Loading.Title");
        _cancel.Text = Localization.T("Common.Cancel");
    }

    public IProgress<ProgressInfo> Progress { get; } = new ProgressShim();

    sealed class ProgressShim : IProgress<ProgressInfo>
    {
        public LoadingDialog? Owner;
        long _lastReportTicks;

        public void Report(ProgressInfo value)
        {
            if (Owner == null || Owner.IsDisposed)
                return;
            if (!Owner.IsHandleCreated)
                return;
            // Throttle to ~30 fps. For a 100M-element parse the producer
            // fires ~10k progress reports; without this throttle every one
            // BeginInvokes the UI thread and the dialog stops repainting /
            // responding to Cancel until the work completes. Step transitions
            // and completion (Progress >= 1.0) always go through.
            bool throttle = value.CurrentStep == 3 && value.Progress > 0 && value.Progress < 1.0;
            if (throttle)
            {
                long now = Environment.TickCount64;
                if (now - _lastReportTicks < 33)
                    return;
                _lastReportTicks = now;
            }
            Owner.BeginInvoke(new Action(() => Owner.Apply(value)));
        }
    }

    public new void Show(IWin32Window? owner)
    {
        ((ProgressShim)Progress).Owner = this;
        CenterToOwner(owner);
        base.Show(owner);
    }

    public new DialogResult ShowDialog(IWin32Window? owner)
    {
        ((ProgressShim)Progress).Owner = this;
        CenterToOwner(owner);
        return base.ShowDialog(owner);
    }

    void CenterToOwner(IWin32Window? owner)
    {
        if (owner is not Control control)
            return;

        var form = control.FindForm();
        if (form == null)
            return;

        StartPosition = FormStartPosition.Manual;
        var ownerBounds = form.Bounds;
        Location = new Point(
            ownerBounds.Left + (ownerBounds.Width - Width) / 2,
            ownerBounds.Top + (ownerBounds.Height - Height) / 2
        );
    }

    public void Apply(ProgressInfo info)
    {
        string sizeStr = FormatThousands(info.Size);
        string name = FileName;
        string text = info.CurrentStep switch
        {
            1 => Localization.F("Loading.Progress.File", 1, info.TotalSteps, name),
            2 => Localization.F("Loading.Progress.Size", 2, info.TotalSteps, name),
            3 => Localization.F("Loading.Progress.Rendering", 3, info.TotalSteps, sizeStr, name),
            _ => Localization.F("Loading.Progress.Unknown", info.CurrentStep, info.TotalSteps),
        };
        _info.Text = text;
        if (info.CurrentStep == 3)
        {
            _indeterminate.Visible = false;
            _determinate.Visible = true;
            int v = (int)Math.Round(info.Progress * 10_000);
            _determinate.Value = Math.Clamp(v, 0, 10_000);
        }
        else
        {
            _indeterminate.Visible = true;
            _determinate.Visible = false;
        }
    }

    static string FormatThousands(int n) =>
        n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
}
