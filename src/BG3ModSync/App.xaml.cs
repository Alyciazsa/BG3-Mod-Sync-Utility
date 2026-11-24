using System;
using System.Threading;
using System.Windows;
using BG3ModSync.Services;

namespace BG3ModSync;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                if (Current?.MainWindow is MainWindow mw)
                {
                    mw.AppendLog($"[WARN] Recovered from UI error: {args.Exception.Message}");
                }
            }
            catch { }
        };

        RuntimeTempCleanupService.CleanupStaleSessionsOnStartup();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RuntimeTempCleanupService.CleanupAllOnExit();
        base.OnStartup(e);
    }

    public static bool TrySetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, false);
                return true;
            }
            catch
            {
                Thread.Sleep(25);
            }
        }
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RuntimeTempCleanupService.CleanupAllOnExit();
        base.OnExit(e);
        Environment.Exit(0);
    }
}
