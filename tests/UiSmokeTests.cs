using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

internal static class UiSmokeTests
{
    static int failures;
    static void Check(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static T Field<T>(MainForm form, string name)
    { return (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form); }
    static PortalSettings Read(MainForm form)
    { return (PortalSettings)typeof(MainForm).GetMethod("ReadSettings", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null); }
    static void SaveOffline(MainForm form)
    {
        Assert((bool)typeof(MainForm).GetMethod("SaveValues", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null), "offline settings save failed");
    }
    static void StartPasswordEdit(TextBox input)
    {
        typeof(Control).GetMethod("OnKeyPress", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(input, new object[] { new KeyPressEventArgs('x') });
    }
    static bool ContainsText(Control root, string text)
    {
        if (root.Text.Contains(text)) return true;
        foreach (Control child in root.Controls) if (ContainsText(child, text)) return true;
        return false;
    }
    sealed class HostSource : IWifiSearchSource
    {
        internal int ReadCalls, SearchCalls;
        internal bool FailSearch;
        internal ManualResetEvent SearchGate;
        internal WifiProfileSnapshot Snapshot = Host("A");
        public WifiProfileSnapshot Read() { ReadCalls++; return Snapshot; }
        public WifiProfileSnapshot Search()
        {
            SearchCalls++;
            if (SearchGate != null && !SearchGate.WaitOne(2000)) throw new TimeoutException("fixture search gate");
            if (FailSearch) throw new InvalidOperationException("fixture unavailable");
            return Snapshot;
        }
    }
    static WifiProfileSnapshot Host(string id)
    {
        return new WifiProfileSnapshot {
            Profiles = new List<string> { "Fixture profile " + id }, ConnectedProfile = "Fixture profile " + id,
            QuerySucceeded = true, Notice = "已获取当前电脑附近的 Wi-Fi。",
            Networks = new List<WifiNetworkChoice> {
                new WifiNetworkChoice { Ssid = "Fixture campus " + id, ProfileName = "Fixture profile " + id, SignalQuality = 80 },
                new WifiNetworkChoice { Ssid = "Fixture unsaved " + id, ProfileName = "", SignalQuality = 40 }
            }
        };
    }
    static void Search(MainForm form)
    {
        var button = Field<GlassButton>(form, "searchWifi");
        Assert(button.Text == "搜索 Wi-Fi", "search action is unclear");
        button.PerformClick();
        WaitForSearch(form);
    }
    static void WaitForSearch(MainForm form)
    {
        var button = Field<GlassButton>(form, "searchWifi");
        var deadline = Stopwatch.StartNew();
        while (!button.Enabled && deadline.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(10); }
        Application.DoEvents();
        Assert(button.Enabled, "search never completed");
    }
    [STAThread]
    static int Main()
    {
        if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAMPUSFLOW_COMMUNITY_DATA_DIR")))
        { Console.WriteLine("FAIL test data directory was not isolated"); return 1; }
        Directory.CreateDirectory(AppData.DirectoryPath);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var host = new HostSource();
        using (var form = new MainForm(false, host))
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
            Check("first run has one blank account and the NJUCM portal default", delegate {
                Assert(Field<TextBox>(form, "username").Text == "" && Field<TextBox>(form, "password").Text == "", "prefilled account");
                Assert(Read(form).LoginUrl == "http://net.njucm.edu.cn/api/portal/v1/login" && Read(form).WifiProfile == "Fixture profile A", "NJUCM portal or runtime host profile missing");
                Assert(host.ReadCalls == 1 && host.SearchCalls == 0, "opening the interface actively scanned");
                Assert(typeof(MainForm).GetField("wifiFallback", BindingFlags.Instance | BindingFlags.NonPublic) == null, "backup Wi-Fi control remains");
            });
            Check("authentication protocol is built in without an advanced settings tab", delegate {
                Field<TextBox>(form, "loginUrl").Text = "https://auth.example.invalid/login";
                Field<CheckBox>(form, "repairStartupProxy").Checked = false;
                var settings = Read(form);
                Assert(settings.Validate() == null, "interface produced invalid configuration");
                Assert(settings.RequestEncoding == "Json" && settings.UsernameField == "username" && settings.PasswordField == "password"
                    && settings.ExtraFields == "domain=default", "known authentication preset not applied");
                Assert(settings.SuccessField == "reply_code" && settings.SuccessValue == "0" && !settings.RepairStartupProxy, "built-in success policy lost");
                Assert(typeof(MainForm).GetField("requestEncoding", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                    "advanced protocol controls remain");
            });
            Check("only simple account log and about pages are shown", delegate {
                Assert(!Field<TextBox>(form, "password").UseSystemPasswordChar && Field<TextBox>(form, "password").PasswordChar == '*', "password is not masked using asterisks");
                var tabs = Field<TabControl>(form, "tabs");
                Assert(tabs.TabCount == 3, "advanced protocol tab remains");
                Assert(Field<ComboBox>(form, "wifiProfile").DropDownStyle == ComboBoxStyle.DropDownList,
                    "Wi-Fi choices can be invented rather than discovered");
            });
            Check("search button obtains visible SSIDs from the running host provider", delegate {
                host.Snapshot = Host("B");
                Search(form);
                var choices = Field<ComboBox>(form, "wifiProfile");
                Assert(host.SearchCalls == 1 && choices.Items.Count == 2, "button did not search the running host");
                foreach (WifiNetworkChoice item in choices.Items)
                    Assert(item.Ssid.EndsWith("B"), "results contain another computer's network list");
                Assert(Read(form).WifiProfile == "Fixture profile B", "SSID was saved instead of its local connection profile");
            });
            Check("an unsaved nearby SSID is shown but cannot enable automatic connection", delegate {
                var choices = Field<ComboBox>(form, "wifiProfile");
                WifiNetworkChoice unsaved = null;
                foreach (WifiNetworkChoice item in choices.Items) if (!item.IsSaved) unsaved = item;
                Assert(unsaved != null, "nearby unsaved network is hidden");
                choices.SelectedItem = unsaved;
                string error = (string)typeof(MainForm).GetMethod("WifiSelectionError", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                Assert(error != null && error.Contains("Windows"), "unsaved SSID can be treated as a usable profile");
                Assert(Read(form).Validate() != null, "automatic connection accepted an unsaved SSID");
            });
            Check("authentication-only mode requires no SSID and disables wireless forcing", delegate {
                Field<CheckBox>(form, "forceConnect").Checked = true;
                Field<CheckBox>(form, "autoConnectWifi").Checked = false;
                PortalSettings settings = Read(form);
                Assert(!settings.AutoConnectWifi && settings.WifiProfile == "" && !settings.ForceConnect && settings.Validate() == null,
                    "authentication-only mode still requires a wireless target");
                Assert(!Field<ComboBox>(form, "wifiProfile").Enabled && !Field<CheckBox>(form, "forceConnect").Enabled,
                    "wireless controls remain active in authentication-only mode");
            });
            Check("search failure clears stale results and allows a retry", delegate {
                host.FailSearch = true;
                Search(form);
                Assert(Field<ComboBox>(form, "wifiProfile").Items.Count == 0, "failed search reused old host results");
                host.FailSearch = false; host.Snapshot = Host("A"); Search(form);
                Field<CheckBox>(form, "autoConnectWifi").Checked = true;
                Assert(Read(form).WifiProfile == "Fixture profile A", "retry did not recover the runtime host results");
            });
            Check("an unrelated visible network is never selected just because it is first", delegate {
                host.Snapshot = Host("C"); host.Snapshot.ConnectedProfile = null;
                Search(form);
                Assert(Field<ComboBox>(form, "wifiProfile").SelectedIndex == -1, "first unrelated network was selected automatically");
                host.Snapshot = Host("A"); Search(form);
            });
            Check("a selection made during search is not reverted by the search result", delegate {
                host.Snapshot = Host("A");
                host.Snapshot.Profiles.Add("Fixture guest profile");
                host.Snapshot.Networks.Add(new WifiNetworkChoice { Ssid = "Fixture guest SSID", ProfileName = "Fixture guest profile" });
                Search(form);
                using (var gate = new ManualResetEvent(false))
                {
                    host.SearchGate = gate;
                    Field<GlassButton>(form, "searchWifi").PerformClick();
                    foreach (WifiNetworkChoice item in Field<ComboBox>(form, "wifiProfile").Items)
                        if (item.ProfileName == "Fixture guest profile") { Field<ComboBox>(form, "wifiProfile").SelectedItem = item; break; }
                    gate.Set(); WaitForSearch(form); host.SearchGate = null;
                }
                Assert(Read(form).WifiProfile == "Fixture guest profile", "search restored an earlier selection over the user's newer choice");
                host.Snapshot = Host("A"); Search(form);
            });
            Check("theme changes do not persist other unsaved edits", delegate {
                typeof(MainForm).GetMethod("ToggleTheme", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                Assert(AppData.LoadSettings().LoginUrl == "http://net.njucm.edu.cn/api/portal/v1/login", "theme persisted an unsaved endpoint");
                Assert(!File.Exists(AppData.CredentialPath), "opening interface saved credentials");
            });
            Check("CampusRelink branding includes an embedded icon and school adaptation notice", delegate {
                Assert(form.Text == "CampusRelink" && ContainsText(form, "本软件默认适配南京中医药大学的校园网认证。"), "branding or school notice missing");
                using (Stream icon = typeof(MainForm).Assembly.GetManifestResourceStream("CampusRelink.AppIcon"))
                {
                    Assert(icon != null && form.Icon != null, "embedded application icon missing");
                    using (var expected = new Icon(icon, 32, 32))
                    using (var expectedImage = expected.ToBitmap())
                    using (var actualImage = new Icon(form.Icon, 32, 32).ToBitmap())
                    {
                        Assert(expectedImage.GetPixel(16, 28).ToArgb() == actualImage.GetPixel(16, 28).ToArgb(), "form uses a different application icon");
                        Color center = actualImage.GetPixel(16, 23);
                        Assert(center.R < 50 && center.G >= 40 && center.G < 90 && center.B >= 60 && center.B < 120,
                            "icon pixel data is corrupt instead of deep blue");
                    }
                }
                PictureBox aboutMark = null;
                foreach (Control child in Field<TabControl>(form, "tabs").TabPages[2].Controls[0].Controls)
                    if (child is PictureBox) aboutMark = (PictureBox)child;
                Assert(aboutMark != null && aboutMark.Image != null, "about icon missing");
                var bitmap = (Bitmap)aboutMark.Image;
                Color background = bitmap.GetPixel(32, 46);
                Assert(background.R < 50 && background.G >= 40 && background.G < 90 && background.B >= 60 && background.B < 120,
                    "about icon is corrupt");
            });
            Check("saving credentials displays fifteen stars and repeated saving preserves the secret", delegate {
                Field<CheckBox>(form, "autoConnectWifi").Checked = false;
                Field<TextBox>(form, "username").Text = "fixture-user";
                Field<TextBox>(form, "password").Text = "fixture-real-password";
                SaveOffline(form);
                Assert(Field<TextBox>(form, "password").Text == new String('*', 15), "saved password field is blank or exposes its actual length");
                SaveOffline(form);
                Assert(CredentialStore.Load().Password == "fixture-real-password", "placeholder was saved as the real password");
            });
            Check("reopening displays the saved placeholder and editing replaces the whole placeholder", delegate {
                using (var reopened = new MainForm(false, new HostSource()))
                {
                    TextBox input = Field<TextBox>(reopened, "password");
                    Assert(input.Text == new String('*', 15), "saved password placeholder not restored");
                    StartPasswordEdit(input);
                    Assert(input.Text == "", "typing appends to the saved placeholder");
                    input.Text = "fixture-new-password";
                    SaveOffline(reopened);
                    Assert(CredentialStore.Load().Password == "fixture-new-password" && input.Text == new String('*', 15), "replacement password not saved or masked");
                }
            });
            Check("a deliberately entered fifteen-star password is not confused with the saved placeholder", delegate {
                TextBox input = Field<TextBox>(form, "password");
                StartPasswordEdit(input);
                input.Text = new String('*', 15);
                SaveOffline(form);
                Assert(CredentialStore.Load().Password == new String('*', 15), "literal asterisk password was silently ignored");
            });
            Check("a saved placeholder cannot reuse credentials for a changed username", delegate {
                TextBox input = Field<TextBox>(form, "password");
                string pending = (string)input.GetType().GetProperty("EnteredPassword", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(input, null);
                bool rejected = false;
                try { CredentialEditor.Build(CredentialStore.Load(), "different-user", pending); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected && pending == "", "saved placeholder allowed another username to inherit the old password");
            });
            Check("all configuration pages render offscreen with their controls", delegate {
                form.CreateControl();
                var tabs = Field<TabControl>(form, "tabs");
                for (int index = 0; index < tabs.TabCount; index++)
                {
                    typeof(MainForm).GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { index });
                    form.PerformLayout();
                    Application.DoEvents();
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, form.ClientRectangle);
                        Assert(tabs.TabPages[index].Controls.Count > 0 && tabs.TabPages[index].Controls[0].Visible,
                            "page controls were not rendered");
                        Assert(tabs.TabPages[index].Controls[0].Right <= tabs.TabPages[index].ClientSize.Width,
                            "page " + index + " card right=" + tabs.TabPages[index].Controls[0].Right
                            + " exceeds page width=" + tabs.TabPages[index].ClientSize.Width);
                        bitmap.Save(Path.Combine(AppData.DirectoryPath, "page-" + index + ".png"));
                    }
                }
                Assert(File.Exists(Path.Combine(AppData.DirectoryPath, "page-1.png")), "render output missing");
            });
        }
        return failures == 0 ? 0 : 1;
    }
}
