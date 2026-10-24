using System;
using System.IO;
using System.Text;
using System.Threading;

internal static class AppData
{
    // Keep the Community identity so CampusRelink upgrades reuse encrypted data,
    // startup registration and background signals without creating duplicates.
    internal const string AppName = "CampusFlowCommunity";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunName = "CampusFlowCommunity";
    internal const string MutexName = @"Local\CampusFlow.Community.SingleInstance";
    internal const string StopEventName = @"Local\CampusFlow.Community.Stop";
    internal const string ConfigurationMutexName = @"Local\CampusFlow.Community.Configuration";
    internal static readonly string DirectoryPath = ResolveDirectory();
    internal static readonly string SettingsPath = Path.Combine(DirectoryPath, "settings.xml");
    internal static readonly string CredentialPath = Path.Combine(DirectoryPath, "credentials.dat");
    internal static readonly string LogPath = Path.Combine(DirectoryPath, "campusflow.log");
    static readonly object LogGate = new object();

    static string ResolveDirectory()
    {
        string custom = Environment.GetEnvironmentVariable("CAMPUSFLOW_COMMUNITY_DATA_DIR");
        return String.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName)
            : Path.GetFullPath(custom);
    }
    internal static PortalSettings LoadSettings() { return PortalSettings.Load(SettingsPath); }
    internal static void SaveSettings(PortalSettings settings) { PortalSettings.Save(SettingsPath, settings); }
    internal static void SaveTheme(bool nightMode)
    {
        WithConfigurationLock(delegate {
            PortalSettings saved = LoadSettings();
            saved.NightMode = nightMode;
            SaveSettings(saved);
        });
    }
    // Both files are read under the same cross-process lock. The encrypted URL
    // binding also rejects a half-written update after a process or disk failure.
    internal static void SaveConnection(PortalSettings settings, CredentialAccount credential)
    {
        if (settings != null) BuiltInAuthentication.Apply(settings);
        string error = settings == null ? "请先配置认证接口。" : settings.Validate();
        if (error != null) throw new InvalidOperationException(error);
        if (credential == null || !credential.IsComplete) throw new InvalidOperationException("请输入完整的账号和密码。");
        WithConfigurationLock(delegate {
            credential.BoundLoginUrl = settings.LoginUrl;
            CredentialStore.Save(credential);
            SaveSettings(settings);
        });
    }
    internal static bool LoadConnection(out PortalSettings settings, out CredentialAccount credential)
    {
        PortalSettings loadedSettings = null;
        CredentialAccount loadedCredential = null;
        WithConfigurationLock(delegate {
            loadedSettings = LoadSettings();
            loadedCredential = CredentialStore.Load();
        });
        settings = loadedSettings;
        credential = loadedCredential;
        bool bound = credential.IsComplete && String.Equals(credential.BoundLoginUrl, settings.LoginUrl, StringComparison.Ordinal);
        if (!bound) credential = new CredentialAccount();
        return bound;
    }
    static void WithConfigurationLock(Action action)
    {
        using (var mutex = new Mutex(false, ConfigurationMutexName))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(5000); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("配置正在更新，请稍后再试。");
            try { action(); }
            finally { mutex.ReleaseMutex(); }
        }
    }
    internal static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                {
                    string previous = LogPath + ".previous";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(LogPath, previous);
                }
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ")
                    + (message ?? "").Replace("\r", " ").Replace("\n", " ") + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }
}
