global using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Afterglow.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--test-child")) { Thread.Sleep(TimeSpan.FromSeconds(30)); return 0; }
        if (args.Contains("--self-test")) return SmokeTest.Run();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        Theme.Install(app);
        app.Run(new MainWindow());
        return 0;
    }
}
