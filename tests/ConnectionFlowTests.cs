using System;
using System.Reflection;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.IO;

internal static class ConnectionFlowTests
{
    static int failed;
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static void Assert(bool value, string message)
    { if (!value) throw new Exception(message); }
    static bool TaskCommandNeedsUpdate(string taskXml, string currentExecutable)
    {
        var method = typeof(StartupManager).GetMethod("TaskCommandNeedsUpdate", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(method != null, "startup task executable migration check is missing");
        try { return (bool)method.Invoke(null, new object[] { taskXml, currentExecutable }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static void RejectTaskCommand(string taskXml)
    {
        bool rejected = false;
        try { TaskCommandNeedsUpdate(taskXml, @"D:\CampusRelink\CampusRelink.exe"); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "an unknown or malformed task command was accepted");
    }
    sealed class EnvironmentFixture : IConnectionEnvironment
    {
        internal int InternetCalls, ConnectCalls, SubmitCalls, Delays;
        internal int OnlineOnCall = Int32.MaxValue;
        internal bool WifiReady = true;
        internal AuthenticationOutcome Outcome = AuthenticationOutcome.Accepted;
        internal string Profile;
        internal bool Force;
        internal CredentialAccount Submitted;
        internal readonly List<string> Logs = new List<string>();
        public bool HasInternet() { return ++InternetCalls >= OnlineOnCall; }
        public bool ConnectWifi(string profile, bool force) { ConnectCalls++; Profile = profile; Force = force; return WifiReady; }
        public AuthenticationOutcome Authenticate(PortalSettings settings, CredentialAccount account)
        { SubmitCalls++; Submitted = account; return Outcome; }
        public void Delay(int milliseconds) { Assert(milliseconds == 1000, "unexpected poll interval"); Delays++; }
        public void Log(string message) { Logs.Add(message); }
    }
    static PortalSettings Settings()
    {
        var settings = PortalSettings.Default();
        settings.LoginUrl = "https://portal.example.invalid/login";
        settings.AutoConnectWifi = true; settings.WifiProfile = "Fixture WiFi";
        return settings;
    }
    static CredentialAccount Account()
    { return new CredentialAccount { Username = "fixture-account", Password = "fixture-secret" }; }
    static int Main()
    {
        if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAMPUSFLOW_COMMUNITY_DATA_DIR")))
        { Console.WriteLine("FAIL test data directory was not isolated"); return 1; }
        Check("release uses a separate data directory", delegate {
            Assert(AppData.AppName == "CampusFlowCommunity", "personal install data would be shared");
        });
        Check("release uses separate background signals", delegate {
            Assert(AppData.MutexName == @"Local\CampusFlow.Community.SingleInstance"
                && AppData.StopEventName == @"Local\CampusFlow.Community.Stop", "background signals would collide");
        });
        Check("release uses separate startup registration", delegate {
            Assert(StartupManager.TaskName == "CampusFlow_Community"
                && AppData.RunName == "CampusFlowCommunity", "startup registration would collide");
        });
        Check("connection flow has an injectable account coordinator", delegate {
            Assert(typeof(Program).Assembly.GetType("ConnectionCoordinator") != null, "single-account coordinator missing");
        });
        Check("single account and one Wi-Fi profile are the only configuration", delegate {
            Assert(typeof(PortalSettings).GetProperty("WifiFallbackProfile") == null, "backup Wi-Fi is still exposed");
            Assert(typeof(Program).Assembly.GetType("CredentialSet") == null, "multiple account store is still exposed");
        });
        Check("invalid configuration performs no connection I/O", delegate {
            var environment = new EnvironmentFixture();
            var invalid = PortalSettings.Default(); invalid.LoginUrl = "not-an-http-url";
            Assert(ConnectionCoordinator.Run(invalid, Account(), environment).StartsWith("配置无效"), "invalid URL accepted");
            Assert(environment.InternetCalls + environment.ConnectCalls + environment.SubmitCalls == 0, "invalid configuration touched network");
        });
        Check("missing credentials perform no connection I/O", delegate {
            var environment = new EnvironmentFixture();
            ConnectionCoordinator.Run(Settings(), new CredentialAccount(), environment);
            Assert(environment.InternetCalls + environment.ConnectCalls + environment.SubmitCalls == 0, "missing account touched network");
        });
        Check("a healthy existing connection is preserved", delegate {
            var environment = new EnvironmentFixture { OnlineOnCall = 1 };
            Assert(ConnectionCoordinator.Run(Settings(), Account(), environment).StartsWith("网络已经"), "healthy connection ignored");
            Assert(environment.ConnectCalls == 0 && environment.SubmitCalls == 0, "healthy connection replaced");
        });
        Check("Wi-Fi failure never submits credentials", delegate {
            var environment = new EnvironmentFixture { WifiReady = false };
            ConnectionCoordinator.Run(Settings(), Account(), environment);
            Assert(environment.ConnectCalls == 1 && environment.SubmitCalls == 0, "submitted on unavailable Wi-Fi");
        });
        Check("activation during association avoids redundant authentication", delegate {
            var environment = new EnvironmentFixture { OnlineOnCall = 2 };
            ConnectionCoordinator.Run(Settings(), Account(), environment);
            Assert(environment.ConnectCalls == 1 && environment.SubmitCalls == 0, "already-online link submitted account");
        });
        Check("accepted authentication submits the configured account once", delegate {
            var environment = new EnvironmentFixture { OnlineOnCall = 3 };
            var settings = Settings(); settings.WifiProfile = "Fixture WiFi"; settings.ForceConnect = true;
            var account = Account();
            Assert(ConnectionCoordinator.Run(settings, account, environment) == "认证成功", "successful authentication not recognized");
            Assert(environment.SubmitCalls == 1 && Object.ReferenceEquals(environment.Submitted, account), "wrong account or multiple submissions");
            Assert(environment.Profile == "Fixture WiFi" && environment.Force, "Wi-Fi options not propagated");
        });
        Check("authentication-only mode never asks the environment to connect Wi-Fi", delegate {
            var environment = new EnvironmentFixture { Outcome = AuthenticationOutcome.Rejected };
            var settings = Settings(); settings.AutoConnectWifi = false; settings.WifiProfile = "";
            ConnectionCoordinator.Run(settings, Account(), environment);
            Assert(environment.ConnectCalls == 0 && environment.SubmitCalls == 1, "authentication-only mode touched Wi-Fi or omitted authentication");
        });
        Check("delayed network activation never resubmits authentication", delegate {
            var environment = new EnvironmentFixture();
            Assert(ConnectionCoordinator.Run(Settings(), Account(), environment).StartsWith("认证已接受"), "accepted result was discarded");
            Assert(environment.SubmitCalls == 1 && environment.Delays == 12, "accepted response was submitted again or polling unbounded");
        });
        Check("explicit rejection submits the single account once", delegate {
            var environment = new EnvironmentFixture { Outcome = AuthenticationOutcome.Rejected };
            Assert(ConnectionCoordinator.Run(Settings(), Account(), environment).StartsWith("认证被拒绝"), "rejection hidden");
            Assert(environment.SubmitCalls == 1 && environment.Delays == 0, "rejected account retried in same cycle");
        });
        Check("unknown replies submit the single account once", delegate {
            var environment = new EnvironmentFixture { Outcome = AuthenticationOutcome.Unknown };
            Assert(ConnectionCoordinator.Run(Settings(), Account(), environment).StartsWith("认证结果无法确认"), "unknown result accepted");
            Assert(environment.SubmitCalls == 1, "unknown result resubmitted");
        });
        Check("transport errors submit the single account once", delegate {
            var environment = new EnvironmentFixture { Outcome = AuthenticationOutcome.Unreachable };
            Assert(ConnectionCoordinator.Run(Settings(), Account(), environment).StartsWith("认证接口暂不可达"), "unreachable result hidden");
            Assert(environment.SubmitCalls == 1, "transport failure resubmitted");
        });
        Check("connection diagnostics contain no credential values", delegate {
            var environment = new EnvironmentFixture { Outcome = AuthenticationOutcome.Rejected };
            var account = Account(); ConnectionCoordinator.Run(Settings(), account, environment);
            string messages = String.Join("\n", environment.Logs.ToArray());
            Assert(!messages.Contains(account.Username) && !messages.Contains(account.Password), "credentials in logs");
        });
        Check("HTTP requests disable proxy inheritance and redirects", delegate {
            var factory = typeof(PortalClient).GetMethod("MakeRequest", BindingFlags.Static | BindingFlags.NonPublic);
            var request = (HttpWebRequest)factory.Invoke(null, new object[] { "https://portal.example.invalid/login", new CookieContainer() });
            Assert(request.Proxy == null && !request.AllowAutoRedirect, "credential transport can inherit proxies or follow redirects");
            Assert(request.Address.AbsoluteUri == "https://portal.example.invalid/login", "configured endpoint changed");
        });
        Check("connectivity accepts only HTTP 200 with the exact expected body", delegate {
            var classify = typeof(PortalClient).GetMethod("IsExpectedConnectivityResponse", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(classify != null, "connectivity response classification is not independently verifiable");
            Func<HttpStatusCode,string,bool> online = delegate(HttpStatusCode status, string body) {
                return (bool)classify.Invoke(null, new object[] {status, body, "Microsoft Connect Test"});
            };
            Assert(online(HttpStatusCode.OK, "Microsoft Connect Test") && online(HttpStatusCode.OK, " \r\nMicrosoft Connect Test\n"),
                "standard connectivity body rejected");
            Assert(!online(HttpStatusCode.OK, "<html>Microsoft Connect Test</html>") && !online(HttpStatusCode.OK, "Microsoft Connect Test extra"),
                "captive portal HTML counted as Internet access");
            Assert(!online(HttpStatusCode.Redirect, "Microsoft Connect Test") && !online(HttpStatusCode.Forbidden, "Microsoft Connect Test")
                && !online(HttpStatusCode.OK, null) && !online(HttpStatusCode.OK, "microsoft connect test"), "nonmatching probe counted as online");
        });
        Check("connectivity probe sends no credentials and uses direct HTTP without redirects", delegate {
            var factory = typeof(PortalClient).GetMethod("MakeRequest", BindingFlags.Static | BindingFlags.NonPublic);
            var request = (HttpWebRequest)factory.Invoke(null, new object[] {PortalSettings.Default().ConnectivityUrl, new CookieContainer()});
            Assert(request.Address.AbsoluteUri == "http://www.msftconnecttest.com/connecttest.txt" && request.Method == "GET",
                "connectivity probe no longer uses the working HTTP GET contract");
            Assert(request.Proxy == null && request.Credentials == null && !request.UseDefaultCredentials && !request.AllowAutoRedirect,
                "connectivity probe leaks credentials uses proxies or follows portal redirects");
        });
        Check("connection settings and credentials are loaded as a bound snapshot", delegate {
            Assert(typeof(AppData).GetMethod("LoadConnection", BindingFlags.Static | BindingFlags.NonPublic) != null,
                "configuration can mix a newly saved password with an older login endpoint");
            var first = Settings();
            var firstAccount = Account();
            AppData.SaveConnection(first, firstAccount);
            PortalSettings snapshotSettings;
            CredentialAccount snapshotAccount;
            Assert(AppData.LoadConnection(out snapshotSettings, out snapshotAccount), "saved snapshot was refused");
            var second = Settings(); second.LoginUrl = "https://second.example.invalid/auth";
            AppData.SaveConnection(second, new CredentialAccount { Username = "second-fixture", Password = "second-secret" });
            Assert(snapshotSettings.LoginUrl == first.LoginUrl && snapshotAccount.Username == firstAccount.Username,
                "an in-flight snapshot changed after a save");
            PortalSettings currentSettings;
            CredentialAccount currentAccount;
            Assert(AppData.LoadConnection(out currentSettings, out currentAccount) && currentSettings.LoginUrl == second.LoginUrl
                && currentAccount.Username == "second-fixture", "updated snapshot mixed old and new values");
            AppData.SaveSettings(first);
            Assert(!AppData.LoadConnection(out currentSettings, out currentAccount) && !currentAccount.IsComplete,
                "a half-written or manually changed endpoint accepted a credential for another endpoint");
        });
        Check("saved Wi-Fi profile parsing does not confuse the SSID with its profile", delegate {
            MethodInfo parser = typeof(WifiManager).GetMethod("ParseProfile", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(parser != null, "saved Wi-Fi profile parser missing");
            string output = "    Name : Wi-Fi\r\n    State : connected\r\n    SSID : Campus Signal\r\n    Profile : Saved Campus Profile\r\n";
            Assert((string)parser.Invoke(null, new object[] { output }) == "Saved Campus Profile", "SSID used as saved profile name");
            output = "    名称 : 无线\n    状态 : 已连接\n    SSID : 校园信号\n    配置文件 : 已保存校园配置\n";
            Assert((string)parser.Invoke(null, new object[] { output }) == "已保存校园配置", "Chinese profile field not recognized");
        });
        Check("Wi-Fi connection commands preserve exact profile identity and reject argument injection", delegate {
            Assert(WifiManager.ConnectionArguments(" Campus ") == "wlan connect name=\" Campus \"", "intentional profile spaces were removed");
            Assert(WifiManager.ConnectionArguments("校园 配置") == "wlan connect name=\"校园 配置\"", "Unicode profile identity changed");
            bool rejected = false;
            try { WifiManager.ConnectionArguments("Campus\" interface=other"); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "quoted profile altered netsh arguments");
        });
        Check("startup task query failures never count as confirmed absence", delegate {
            var classifier = typeof(StartupManager).GetMethod("ClassifyTaskQueryError", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(classifier != null, "task absence and failed queries are indistinguishable");
            Assert(classifier.Invoke(null, new object[] { new COMException("missing", unchecked((int)0x80070002)) }).ToString() == "Absent", "missing task not recognized");
            Assert(classifier.Invoke(null, new object[] { new FileNotFoundException("missing") }).ToString() == "Absent", "native CLR file-not-found mapping not recognized");
            Assert(classifier.Invoke(null, new object[] { new COMException("denied", unchecked((int)0x80070005)) }).ToString() == "Unknown", "access denial treated as absent");
            Assert(classifier.Invoke(null, new object[] { new InvalidOperationException("fixture") }).ToString() == "Unknown", "query exception treated as absent");
        });
        Check("renamed executable refreshes the existing elevated startup task", delegate {
            const string xml = "<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command>D:\\CampusFlow\\CampusFlow.exe</Command><Arguments>--background --startup</Arguments></Exec></Actions></Task>";
            Assert(TaskCommandNeedsUpdate(xml, @"D:\CampusRelink\CampusRelink.exe"), "old CampusFlow executable was retained for the next logon");
        });
        Check("matching Windows executable paths do not request elevated task registration again", delegate {
            const string xml = "<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command>d:/CampusRelink/subdir/../CAMPUSRELINK.exe</Command></Exec></Actions></Task>";
            Assert(!TaskCommandNeedsUpdate(xml, @"D:\CampusRelink\CampusRelink.exe"), "equivalent Windows executable paths request unnecessary elevation");
            const string uncXml = "<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command>\\\\fixture-server\\apps\\CampusRelink.exe</Command></Exec></Actions></Task>";
            Assert(!TaskCommandNeedsUpdate(uncXml, @"\\fixture-server\apps\CampusRelink.exe"), "UNC executable path was not recognized");
            const string quotedXml = "<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command>&quot;D:\\Campus Relink\\CampusRelink.exe&quot;</Command></Exec></Actions></Task>";
            Assert(!TaskCommandNeedsUpdate(quotedXml, @"D:\Campus Relink\CampusRelink.exe"), "quoted executable path with spaces was not recognized");
        });
        Check("unknown empty or malformed startup commands never count as a matching executable", delegate {
            RejectTaskCommand(null);
            RejectTaskCommand("");
            RejectTaskCommand("<Task>");
            RejectTaskCommand("<Task><Actions><Exec><Command>D:\\CampusRelink\\CampusRelink.exe</Command></Exec></Actions></Task>");
            RejectTaskCommand("<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command /></Exec></Actions></Task>");
            RejectTaskCommand("<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command>D:\\CampusRelink\\CampusRelink.exe</Command></Exec><Exec><Command>D:\\Other.exe</Command></Exec></Actions></Task>");
            RejectTaskCommand("<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command>CampusRelink.exe</Command></Exec></Actions></Task>");
            RejectTaskCommand("<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command>D:CampusRelink.exe</Command></Exec></Actions></Task>");
            RejectTaskCommand("<!DOCTYPE Task [<!ENTITY command 'D:\\CampusRelink\\CampusRelink.exe'>]><Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Actions><Exec><Command>&command;</Command></Exec></Actions></Task>");
        });
        Check("native task scheduler read confirms a known community task state", delegate {
            var query = typeof(StartupManager).GetMethod("QueryElevatedTask", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(query.Invoke(null, null).ToString() != "Unknown", "native task query did not recognize the Windows response");
        });
        return failed == 0 ? 0 : 1;
    }
}
