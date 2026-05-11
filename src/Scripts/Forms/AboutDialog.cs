using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Jasnote.Core;

namespace Jasnote.Forms;

public sealed class AboutDialog : Form, ILocalizable
{
    readonly Label _versionLabel = new() { AutoSize = true, Location = new Point(22, 56) };
    readonly Label _descriptionLabel = new() { AutoSize = true, Location = new Point(22, 110) };
    readonly Button _ok =
        new()
        {
            DialogResult = DialogResult.OK,
            Location = new Point(340, 174),
            Width = 80,
        };

    public AboutDialog()
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(440, 220);

        var title = new Label
        {
            Text = AppInfo.AppName,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 16, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 20),
        };
        var copy = new Label
        {
            Text = AppInfo.Copyright,
            AutoSize = true,
            Location = new Point(22, 78),
        };
        var link = new LinkLabel
        {
            Text = AppInfo.HomeUrl,
            AutoSize = true,
            Location = new Point(22, 134),
        };
        link.LinkClicked += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppInfo.HomeUrl) { UseShellExecute = true });
            }
            catch { }
        };
        AcceptButton = _ok;

        Controls.Add(title);
        Controls.Add(_versionLabel);
        Controls.Add(copy);
        Controls.Add(_descriptionLabel);
        Controls.Add(link);
        Controls.Add(_ok);

        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        Text = Localization.T("About.Title");
        _versionLabel.Text = Localization.F("About.Version", GlobalSettings.Version);
        _descriptionLabel.Text = Localization.T("About.Description");
        _ok.Text = Localization.T("Common.OK");
    }
}
