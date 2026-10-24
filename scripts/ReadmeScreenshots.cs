using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Documentation screenshots use the real UI and only synthetic credentials/WLAN
// observations. This entry point never enables startup, scans Wi-Fi or logs in.
internal static class ReadmeScreenshots
{
    sealed class DemoWifi : IWifiProfileSource
    {
        public WifiProfileSnapshot Read()
        {
            return new WifiProfileSnapshot {
                QuerySucceeded = true, ConnectedProfile = "Campus-WiFi (Demo)",
                Profiles = new List<string> { "Campus-WiFi (Demo)" },
                Notice = "演示数据：非本机扫描结果。",
                Networks = new List<WifiNetworkChoice> {
                    new WifiNetworkChoice { Ssid = "Campus-WiFi (Demo)", ProfileName = "Campus-WiFi (Demo)", SignalQuality = 86 }
                }
            };
        }
    }

    static void Capture(MainForm form, string target, int page)
    {
        typeof(MainForm).GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { page });
        ((Label)typeof(MainForm).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form)).Text = "○ 后台未启用";
        form.PerformLayout();
        Application.DoEvents();
        using (var image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, form.ClientRectangle);
            image.Save(target, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAMPUSFLOW_COMMUNITY_DATA_DIR")))
                throw new InvalidOperationException("Use Capture-ReadmeScreenshots.ps1 with an isolated data directory.");
            string output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            foreach (bool dark in new[] { false, true })
            {
                PortalSettings settings = PortalSettings.Default();
                settings.NightMode = dark;
                settings.AutoConnectWifi = true;
                settings.WifiProfile = "Campus-WiFi (Demo)";
                AppData.SaveConnection(settings, new CredentialAccount { Username = "demo-student", Password = "demo-only-password" });
                using (var form = new MainForm(false, new DemoWifi()))
                {
                    form.ShowInTaskbar = false;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-32000, -32000);
                    form.Show();
                    Capture(form, Path.Combine(output, dark ? "main-dark.png" : "main-light.png"), 0);
                    if (!dark) Capture(form, Path.Combine(output, "about.png"), 2);
                }
            }
            Console.WriteLine("Captured three real-UI screenshots using demo-only account and Wi-Fi data. No authentication, Wi-Fi scan or network setting change executed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
