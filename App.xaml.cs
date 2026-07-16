using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace WebStream;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\WebStream.Radio.MainInstance";
    private const string ActivateEventName = @"Local\WebStream.Radio.ActivateInstance";
    private const int SwRestore = 9;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;

    protected override async void OnStartup(StartupEventArgs e)
    {
        LocalizationManager.Initialize();
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

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            SignalExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var window = new MainWindow();
        window.Show();
        StartActivationListener(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _activateEvent?.Dispose();
        base.OnExit(e);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activateEvent.Set();
            return;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        ActivateExistingWindowHandle();
    }

    private void StartActivationListener(MainWindow window)
    {
        var activateEvent = _activateEvent;
        if (activateEvent is null) return;

        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    activateEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.Invoke(() =>
                {
                    window.Show();
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;

                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;
                });
            }
        });
    }

    private static void ActivateExistingWindowHandle()
    {
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id) continue;
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) continue;

            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
            return;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
