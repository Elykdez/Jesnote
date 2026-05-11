using Jasnote.Forms;

namespace Jasnote;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var settings = AppSettings.Load();
        Localization.Apply(settings.Language);
        var form = new MainWindow(settings);
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            form.LoadInitial(args[0]);
        }
        Application.Run(form);
    }
}
