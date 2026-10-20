using System.Text;

namespace EasyRecorder;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
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
