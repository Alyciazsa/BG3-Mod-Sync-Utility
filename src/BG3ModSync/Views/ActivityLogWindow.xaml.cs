using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BG3ModSync.Views;

public partial class ActivityLogWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly Func<string> _getLogText;
    private readonly Action _clearLogAction;

    public ActivityLogWindow(Func<string> getLogText, Action clearLogAction)
    {
        InitializeComponent();
        _getLogText = getLogText;
        _clearLogAction = clearLogAction;

        SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            catch { }
        };

        Loaded += (_, _) =>
        {
            LogConsoleTextBox.Text = _getLogText();
            LogConsoleTextBox.ScrollToEnd();
        };
    }

    public void AppendLiveLine(string formattedLine)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLiveLine(formattedLine));
            return;
        }
        LogConsoleTextBox.AppendText(formattedLine);
        LogConsoleTextBox.ScrollToEnd();
    }

    public void SetFullText(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetFullText(text));
            return;
        }
        LogConsoleTextBox.Text = text;
        LogConsoleTextBox.ScrollToEnd();
    }

    private void CopyLogBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(LogConsoleTextBox.Text))
        {
            App.TrySetClipboardText(LogConsoleTextBox.Text);
        }
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        _clearLogAction();
        LogConsoleTextBox.Clear();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
