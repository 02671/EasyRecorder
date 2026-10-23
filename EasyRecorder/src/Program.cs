using System.Text;

namespace EasyRecorder;

internal static class Program
{
    private const string MutexName = @"Local\EasyRecorder.SingleInstance";
    private const string ShowEventName = @"Local\EasyRecorder.ShowExisting";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    [STAThread]
    private static void Main()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew)
        {
            _showEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        MainForm form = new();
        Thread watcher = new(() => WatchShowRequests(form))
        {
            IsBackground = true,
            Name = "EasyRecorder.ShowExisting"
        };
        watcher.Start();
        Application.Run(form);
    }

    private static void WatchShowRequests(MainForm form)
    {
        while (true)
        {
            _showEvent!.WaitOne();
            try
            {
                if (!form.IsDisposed && form.IsHandleCreated)
                {
                    form.BeginInvoke(form.RestoreFromSecondInstance);
                }
            }
            catch
            {
            }
        }
    }

    private static void ReportFatal(Exception? ex)
    {
        if (ex == null)
        {
            return;
        }

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyRecorder");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }

        MessageBox.Show(
            "程序遇到错误：\n\n" + ex.Message + "\n\n详细信息已记录到：\n" + Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyRecorder",
                "error.log"),
            "EasyRecorder",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
