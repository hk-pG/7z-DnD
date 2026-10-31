namespace SevenZipDnD;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        AppLog.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.WriteException("UI_THREAD", e.Exception);
            MessageBox.Show(
                "予期しないエラーが発生しました。ログファイルを確認してください。\n\n" + e.Exception.Message,
                "7z DnD",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLog.WriteException("DOMAIN", ex);
            }
            else
            {
                AppLog.Write($"DOMAIN: {e.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.WriteException("TASK", e.Exception);
            e.SetObserved();
        };

        Application.Run(new Form1(args));
    }
}
