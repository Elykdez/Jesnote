using System.Drawing;
using System.Windows.Forms;
using Jasnote.Controls;
using Microsoft.Win32;

namespace Jasnote;

public static class Theme
{
    public sealed class Palette
    {
        public Color Window;
        public Color WindowText;
        public Color Control;
        public Color ControlText;
        public Color ControlDark;
        public Color KeyText;
        public Color ContainerText;
        public Color StringText;
        public Color NumberText;
        public Color BoolNullText;
        public Color GuideLine;
        public Color HighlightBack;
        public Color HighlightText;
    }

    public static readonly Palette Light =
        new()
        {
            Window = Color.White,
            WindowText = Color.Black,
            Control = Color.FromArgb(245, 245, 245),
            ControlText = Color.Black,
            ControlDark = Color.FromArgb(225, 225, 225),
            KeyText = Color.FromArgb(20, 20, 20),
            ContainerText = Color.FromArgb(20, 20, 20),
            StringText = Color.FromArgb(176, 121, 0),
            NumberText = Color.FromArgb(0, 128, 0),
            BoolNullText = Color.FromArgb(192, 32, 32),
            GuideLine = Color.FromArgb(200, 200, 200),
            HighlightBack = Color.FromArgb(0, 120, 215),
            HighlightText = Color.White,
        };

    public static readonly Palette Dark =
        new()
        {
            Window = Color.FromArgb(30, 30, 30),
            WindowText = Color.FromArgb(220, 220, 220),
            Control = Color.FromArgb(45, 45, 48),
            ControlText = Color.FromArgb(220, 220, 220),
            ControlDark = Color.FromArgb(60, 60, 64),
            KeyText = Color.FromArgb(220, 220, 220),
            ContainerText = Color.FromArgb(220, 220, 220),
            StringText = Color.FromArgb(230, 180, 80),
            NumberText = Color.FromArgb(120, 200, 120),
            BoolNullText = Color.FromArgb(230, 110, 110),
            GuideLine = Color.FromArgb(70, 70, 70),
            HighlightBack = Color.FromArgb(38, 79, 120),
            HighlightText = Color.White,
        };

    public static Palette Resolve(ColorThemePreference pref) =>
        pref switch
        {
            ColorThemePreference.Light => Light,
            ColorThemePreference.Dark => Dark,
            _ => IsSystemDark() ? Dark : Light,
        };

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
            );
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0;
        }
        catch { }
        return false;
    }

    public static void Apply(Control root, Palette p)
    {
        ApplyRecursive(root, p);
    }

    static void ApplyRecursive(Control c, Palette p)
    {
        switch (c)
        {
            case VirtualJsonTree tree:
                tree.BackColor = p.Window;
                tree.ForeColor = p.WindowText;
                tree.KeyColor = p.KeyText;
                tree.ContainerColor = p.ContainerText;
                tree.StringColor = p.StringText;
                tree.NumberColor = p.NumberText;
                tree.BoolNullColor = p.BoolNullText;
                tree.GuideColor = p.GuideLine;
                tree.SelectedBack = p.HighlightBack;
                tree.SelectedFore = p.HighlightText;
                break;
            case TextBox tb:
                tb.BackColor = p.Window;
                tb.ForeColor = p.WindowText;
                tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.BackColor = p.Window;
                cb.ForeColor = p.WindowText;
                cb.FlatStyle = FlatStyle.Flat;
                break;
            case MenuStrip ms:
                ms.BackColor = p.Control;
                ms.ForeColor = p.ControlText;
                ms.Renderer = new MenuRenderer(p);
                break;
            case StatusStrip ss:
                ss.BackColor = p.Control;
                ss.ForeColor = p.ControlText;
                ss.Renderer = new MenuRenderer(p);
                break;
            case ToolStrip tspl:
                tspl.BackColor = p.Control;
                tspl.ForeColor = p.ControlText;
                tspl.Renderer = new MenuRenderer(p);
                break;
            case LinkLabel link:
                link.BackColor = Color.Transparent;
                link.ForeColor = p.ControlText;
                link.ActiveLinkColor = p.HighlightBack;
                link.LinkColor = p.HighlightBack;
                link.VisitedLinkColor = p.HighlightBack;
                break;
            case Label l:
                l.BackColor = Color.Transparent;
                l.ForeColor = p.ControlText;
                break;
            case Button b:
                b.BackColor = p.ControlDark;
                b.ForeColor = p.ControlText;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = p.ControlDark;
                break;
            case Panel pn:
                pn.BackColor = p.Control;
                pn.ForeColor = p.ControlText;
                break;
            default:
                if (c is Form)
                {
                    c.BackColor = p.Control;
                    c.ForeColor = p.ControlText;
                }
                break;
        }
        foreach (Control child in c.Controls)
            ApplyRecursive(child, p);
    }

    sealed class MenuRenderer(Theme.Palette p) : ToolStripProfessionalRenderer(new MenuColors(p))
    {
        readonly Palette _p = p;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _p.ControlText : Color.Gray;
            base.OnRenderItemText(e);
        }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        readonly Palette _p;

        public MenuColors(Palette p)
        {
            _p = p;
            UseSystemColors = false;
        }

        public override Color MenuItemSelected => _p.HighlightBack;
        public override Color MenuItemSelectedGradientBegin => _p.HighlightBack;
        public override Color MenuItemSelectedGradientEnd => _p.HighlightBack;
        public override Color MenuItemPressedGradientBegin => _p.HighlightBack;
        public override Color MenuItemPressedGradientEnd => _p.HighlightBack;
        public override Color MenuItemBorder => _p.HighlightBack;
        public override Color MenuStripGradientBegin => _p.Control;
        public override Color MenuStripGradientEnd => _p.Control;
        public override Color ToolStripDropDownBackground => _p.Control;
        public override Color ToolStripBorder => _p.ControlDark;
        public override Color ImageMarginGradientBegin => _p.Control;
        public override Color ImageMarginGradientEnd => _p.Control;
        public override Color ImageMarginGradientMiddle => _p.Control;
        public override Color StatusStripGradientBegin => _p.Control;
        public override Color StatusStripGradientEnd => _p.Control;
        public override Color SeparatorDark => _p.ControlDark;
        public override Color SeparatorLight => _p.ControlDark;
    }
}
