using System.Windows;

namespace WebStream;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--record-song", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (SongRecorderWorker.TryParse(e.Args, out var request))
            {
                try
                {
                    await SongRecorderWorker.RunAsync(request);
                }
                finally
                {
                    Shutdown();
                }
            }
            else
            {
                Shutdown();
            }
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
