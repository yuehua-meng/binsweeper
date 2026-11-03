using System.Drawing;
using System.Windows.Forms;

namespace BinSweeper.App;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconHost(Action onOpen, Action onScan, Action onExit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => onOpen());
        menu.Items.Add("立即扫描", null, (_, _) => onScan());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => onExit());

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "BinSweeper · 回收站小文件清理",
            ContextMenuStrip = menu,
            Visible = false,
        };

        _notifyIcon.DoubleClick += (_, _) => onOpen();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/BinSweeper.ico"))?.Stream;

            return stream is null ? SystemIcons.Application : new Icon(stream, 32, 32);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Show() => _notifyIcon.Visible = true;

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
