using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshWeb;

internal static class Program
{
    private const string Url = "http://127.0.0.1:3080";
    private const int Port = 3080;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    [STAThread]
    private static void Main()
    {
        // 服务未启动时自动拉起（调用同目录下的 start-dsh.vbs 静默启动）
        if (!PortOpen())
        {
            var vbs = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
            if (File.Exists(vbs))
            {
                Process.Start(new ProcessStartInfo("wscript.exe", "\"" + vbs + "\"") { UseShellExecute = true });
                for (var i = 0; i < 90 && !PortOpen(); i++)
                    Thread.Sleep(1000);
            }
            else
            {
                MessageBox.Show("未找到 start-dsh.vbs，无法启动 dsh 服务。", "DeepSeek Harness",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        if (!PortOpen())
        {
            MessageBox.Show("dsh 服务启动超时，请查看日志：%USERPROFILE%\\.dsh-web.log", "DeepSeek Harness",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var icon = LoadEmbeddedIcon();
        var form = new Form
        {
            Text = "DeepSeek Harness",
            ClientSize = new Size(1280, 840),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(800, 600),
            Icon = icon ?? SystemIcons.Application
        };

        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        form.FormClosing += (_, _) =>
        {
            try { web.Dispose(); } catch { /* ignore */ }
            if (icon is not null)
            {
                try { DestroyIcon(icon.Handle); } catch { /* ignore */ }
                icon.Dispose();
            }
        };

        form.Load += async (_, _) =>
        {
            // WebView2 user data goes to %LOCALAPPDATA%\DshWeb to keep the app dir clean
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshWeb", "WebView2");
            web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder };
            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            web.CoreWebView2.Navigate(Url);
        };

        Application.Run(form);
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("favicon.png", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return null;
            using var bmp = new Bitmap(stream);
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return null;
        }
    }

    private static bool PortOpen()
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", Port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
