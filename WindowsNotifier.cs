using System.Drawing;
using System.Threading;
using Forms = System.Windows.Forms;

namespace WebStream;

public static class WindowsNotifier
{
    public static Task ShowAsync(string title, string message, int timeoutMilliseconds = 5000)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new Forms.ApplicationContext();
            using var notifyIcon = new Forms.NotifyIcon
            {
                Icon = SystemIcons.Information,
                Text = "WebStream",
                Visible = true
            };
            using var timer = new Forms.Timer
            {
                Interval = Math.Max(1000, timeoutMilliseconds + 1500)
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                notifyIcon.Visible = false;
                context.ExitThread();
            };

            notifyIcon.BalloonTipClosed += (_, _) =>
            {
                timer.Stop();
                notifyIcon.Visible = false;
                context.ExitThread();
            };

            notifyIcon.BalloonTipClicked += (_, _) =>
            {
                timer.Stop();
                notifyIcon.Visible = false;
                context.ExitThread();
            };

            timer.Start();
            notifyIcon.ShowBalloonTip(timeoutMilliseconds, title, message, Forms.ToolTipIcon.Info);
            Forms.Application.Run(context);
            completion.TrySetResult();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
}
