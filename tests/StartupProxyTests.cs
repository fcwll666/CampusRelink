using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

static class StartupProxyTests
{
    static int failures;

    sealed class FixtureWebResponse : WebResponse
    {
        public override void Close() { }
    }

    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    sealed class FakeEnvironment : IStartupProxyEnvironment
    {
        internal ProxyConfiguration Configuration = new ProxyConfiguration { Flags = 3, Server = "127.0.0.1:7890" };
        internal DateTime Now = new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc);
        internal StartupSession Session;
        internal string DataDirectory = Path.Combine(AppData.DirectoryPath, Guid.NewGuid().ToString("N"));
        internal bool ClientRunning;
        internal bool Cancelled;
        internal bool ReadThrows;
        internal int Claims, Reads, Probes, Delays, Writes;
        internal int PortalProbes;
        internal PortalProbeStatus PortalStatus = PortalProbeStatus.Failed;
        internal Action<FakeEnvironment> OnPortal;
        internal ProxyEndpointStatus Status = ProxyEndpointStatus.Closed;
        internal Action<FakeEnvironment> OnDelay, OnProbe, OnBackup;

        internal FakeEnvironment()
        {
            Session = new StartupSession { BootUtc = Now.AddSeconds(-30), Identity = "fixture-boot-1" };
        }
        public bool TryClaimStartupCheck() { Claims++; return Session.TryClaim(DataDirectory, Now); }
        public bool IsWithinStartupWindow() { return Session.IsCheckActive(Now); }
        public ProxyConfiguration ReadProxy()
        {
            Reads++;
            if (ReadThrows) throw new IOException("fixture read failure");
            return new ProxyConfiguration { Flags = Configuration.Flags, Server = Configuration.Server, Bypass = Configuration.Bypass, AutoConfigUrl = Configuration.AutoConfigUrl };
        }
        public PortalProbeStatus ProbePortal()
        {
            PortalProbes++;
            if (OnPortal != null) OnPortal(this);
            return PortalStatus;
        }
        public bool IsCancellationRequested() { return Cancelled; }
        public void SaveBackup(ProxyConfiguration expected) { if (OnBackup != null) OnBackup(this); }
        public ProxyEndpointStatus Probe(ProxyEndpoint endpoint)
        {
            Probes++;
            if (OnProbe != null) OnProbe(this);
            return Status;
        }
        public void Delay(int milliseconds)
        {
            Assert(milliseconds == 5000, "Unexpected startup grace interval");
            Delays++;
            Now = Now.AddMilliseconds(milliseconds);
            if (OnDelay != null) OnDelay(this);
        }
        // Fake only the OS write. All eligibility, timing, snapshot comparison and
        // startup-marker decisions come from production code, not from this fake.
        public bool ApplyRecovery(ProxyConfiguration expected)
        {
            Writes++;
            Configuration.Flags = StartupProxyRepair.RecoveryFlags(expected.Flags);
            return true;
        }
        public void Log(string message) { }
    }

    static void AssertPreserved(FakeEnvironment environment, string message)
    {
        Assert(!StartupProxyRepair.Run(true, environment), message + " (reported recovery)");
        Assert(environment.Writes == 0, message + " (wrote proxy settings)");
    }

    static int Main()
    {
        // Process-local proxy only. No request is sent and no Windows setting is changed.
        WebRequest.DefaultWebProxy = new WebProxy("http://127.0.0.1:1");
        Check("campus requests bypass a stale system proxy", delegate
        {
            MethodInfo factory = typeof(PortalClient).GetMethod("MakeRequest", BindingFlags.Static | BindingFlags.NonPublic);
            var request = (HttpWebRequest)factory.Invoke(null, new object[] { "http://campus.invalid/", new CookieContainer() });
            Assert(request.Proxy == null, "Campus HTTP requests still inherit the unavailable proxy");
        });
        Check("runtime launch never reads, probes or changes the proxy", delegate
        {
            var environment = new FakeEnvironment();
            Assert(!StartupProxyRepair.Run(false, environment), "Runtime launch recovered proxy");
            Assert(environment.Claims == 0 && environment.Reads == 0 && environment.Probes == 0 && environment.Writes == 0,
                "Runtime launch entered startup recovery");
            Assert(!StartupProxyRepair.IsStartupLaunch(new[] { "--background", "--runtime" }), "Runtime arguments misclassified");
            Assert(!StartupProxyRepair.IsStartupLaunch(new[] { "--background", "--startup", "--runtime" }), "Runtime must override startup");
            Assert(!StartupProxyRepair.IsStartupLaunch(new[] { "--selftest" }), "Login selftest misclassified");
        });
        Check("legacy and new autostart arguments are accepted", delegate
        {
            Assert(StartupProxyRepair.IsStartupLaunch(new[] { "--background" }), "Existing task no longer works");
            Assert(StartupProxyRepair.IsStartupLaunch(new[] { "--background", "--startup" }), "New task misclassified");
        });
        Check("proxy recovery requires exactly the first three failed portal probes", delegate
        {
            var environment = new FakeEnvironment();
            Assert(StartupProxyRepair.Run(true, environment), "Three failed probes did not recover stale proxy");
            Assert(environment.PortalProbes == 3, "Recovery did not wait for exactly three failed portal probes");
            Assert(environment.Writes == 1, "Recovery must write once");
        });
        Check("a portal reached on any of the first three probes preserves the proxy", delegate
        {
            for (int successAt = 1; successAt <= 3; successAt++)
            {
                int attempt = successAt;
                var environment = new FakeEnvironment { OnPortal = delegate(FakeEnvironment e) { if (e.PortalProbes == attempt) e.PortalStatus = PortalProbeStatus.Reached; } };
                AssertPreserved(environment, "A reachable portal still caused proxy recovery");
                Assert(environment.PortalProbes == attempt, "Portal probing did not stop at success");
            }
        });
        Check("unknown portal status never counts as a confirmed failure", delegate
        {
            var environment = new FakeEnvironment { PortalStatus = PortalProbeStatus.Unknown };
            AssertPreserved(environment, "Unknown portal result counted as a failure");
        });
        Check("a running client with no listener still needs three confirmed portal failures", delegate
        {
            var environment = new FakeEnvironment { ClientRunning = true };
            Assert(StartupProxyRepair.Run(true, environment) && environment.PortalProbes == 3,
                "A process name incorrectly replaced the actual listener and portal checks");
        });
        Check("late login cannot reopen the five minute boot window", delegate
        {
            var environment = new FakeEnvironment();
            environment.Session.BootUtc = environment.Now.AddMinutes(-6);
            AssertPreserved(environment, "Login time was mistaken for boot time");
            Assert(environment.PortalProbes == 0, "Late login performed startup probes");
        });
        Check("autostart task definition permits batteries and unlimited background operation", delegate
        {
            MethodInfo builder = typeof(StartupManager).GetMethod("BuildElevatedTaskXml", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(builder != null, "Autostart task still has default battery and runtime limits");
            string path = @"D:\Campus & Test\CampusFlow.exe";
            string xml = (string)builder.Invoke(null, new object[] { path, "S-1-5-21-123-456-789-1001" });
            var document = new System.Xml.XmlDocument();
            document.XmlResolver = null;
            document.LoadXml(xml);
            var ns = new System.Xml.XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            Assert(document.SelectSingleNode("/t:Task/t:Settings/t:DisallowStartIfOnBatteries", ns).InnerText == "false", "Autostart blocked on batteries");
            Assert(document.SelectSingleNode("/t:Task/t:Settings/t:StopIfGoingOnBatteries", ns).InnerText == "false", "Background stops when unplugged");
            Assert(document.SelectSingleNode("/t:Task/t:Settings/t:ExecutionTimeLimit", ns).InnerText == "PT0S", "Background has a time limit");
            Assert(document.SelectSingleNode("/t:Task/t:Triggers/t:LogonTrigger/t:Enabled", ns).InnerText == "true", "Logon trigger not enabled");
            Assert(document.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:RunLevel", ns).InnerText == "HighestAvailable", "Strong connect lost admin startup");
            Assert(document.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Command", ns).InnerText == path, "Executable path not XML escaped correctly");
            Assert(document.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Arguments", ns).InnerText == "--background --startup", "Wrong autostart arguments");
            Assert(document.SelectSingleNode("/t:Task/t:Triggers/t:LogonTrigger/t:UserId", ns).InnerText == "S-1-5-21-123-456-789-1001", "Startup targets the wrong user");
            Assert(document.SelectSingleNode("/t:Task/t:Settings/t:StartWhenAvailable", ns).InnerText == "false", "Saving a task can immediately replay an old logon trigger");
            Assert(document.SelectSingleNode("/t:Task/t:Settings/t:RunOnlyIfNetworkAvailable", ns).InnerText == "false", "Autostart requires Internet before campus login");
            Directory.CreateDirectory(AppData.DirectoryPath);
            File.WriteAllText(Path.Combine(AppData.DirectoryPath, "autostart-task-definition.xml"), xml, System.Text.Encoding.Unicode);
        });
        Check("stale local proxy is recovered once after the startup grace period", delegate
        {
            var environment = new FakeEnvironment();
            Assert(StartupProxyRepair.Run(true, environment), "Stale local proxy was not recovered");
            Assert(environment.Delays == 2 && environment.Probes >= 6 && environment.PortalProbes == 3, "Did not wait and confirm repeatedly");
            Assert(environment.Writes == 1 && environment.Configuration.Flags == 9, "Manual proxy or auto-detect flags incorrect");
            Assert(environment.Configuration.Server == "127.0.0.1:7890", "Proxy address was erased");
            environment.Configuration.Flags = 3;
            Assert(!StartupProxyRepair.Run(true, environment) && environment.Writes == 1, "A second launch changed the proxy again");
        });
        Check("a healthy startup also consumes the single check", delegate
        {
            var environment = new FakeEnvironment { Status = ProxyEndpointStatus.Listening };
            AssertPreserved(environment, "Active proxy changed");
            int reads = environment.Reads;
            environment.Status = ProxyEndpointStatus.Closed;
            AssertPreserved(environment, "Proxy closed during later use was changed");
            Assert(environment.Reads == reads, "The second launch repeated the check");
        });
        Check("a disabled manual proxy also consumes the single check", delegate
        {
            var environment = new FakeEnvironment();
            environment.Configuration.Flags = 9;
            AssertPreserved(environment, "Disabled proxy changed");
            environment.Configuration.Flags = 3;
            AssertPreserved(environment, "A proxy enabled after startup was changed");
        });
        Check("new Windows boot permits a new startup check", delegate
        {
            var environment = new FakeEnvironment { Status = ProxyEndpointStatus.Listening };
            AssertPreserved(environment, "Active proxy changed");
            environment.Session = new StartupSession { Identity = "fixture-boot-2", BootUtc = environment.Now };
            environment.Status = ProxyEndpointStatus.Closed;
            Assert(StartupProxyRepair.Run(true, environment) && environment.Writes == 1, "New boot could not recover stale proxy");
        });
        Check("xxyun with an active listener is preserved without portal requests", delegate
        {
            var environment = new FakeEnvironment { ClientRunning = true, Status = ProxyEndpointStatus.Listening };
            AssertPreserved(environment, "Running xxyun changed");
            Assert(environment.PortalProbes == 0, "An active proxy was sent unnecessary portal requests");
        });
        Check("xxyun starting during grace is preserved", delegate
        {
            var environment = new FakeEnvironment { OnDelay = delegate(FakeEnvironment e) { e.ClientRunning = true; e.Status = ProxyEndpointStatus.Listening; } };
            AssertPreserved(environment, "Starting xxyun changed");
        });
        Check("a port becoming live during grace is preserved", delegate
        {
            var environment = new FakeEnvironment { OnDelay = delegate(FakeEnvironment e) { e.Status = ProxyEndpointStatus.Listening; } };
            AssertPreserved(environment, "Late proxy listener changed");
        });
        Check("unknown socket failures do not trigger repair", delegate
        {
            AssertPreserved(new FakeEnvironment { Status = ProxyEndpointStatus.Unknown }, "Uncertain proxy changed");
        });
        Check("changed address during grace is preserved", delegate
        {
            var environment = new FakeEnvironment { OnDelay = delegate(FakeEnvironment e) { e.Configuration.Server = "127.0.0.1:7891"; } };
            AssertPreserved(environment, "User's new address changed");
        });
        Check("changed flags during grace are preserved", delegate
        {
            var environment = new FakeEnvironment { OnDelay = delegate(FakeEnvironment e) { e.Configuration.Flags = 11; } };
            AssertPreserved(environment, "User's new flags changed");
        });
        Check("changed PAC URL during grace is preserved", delegate
        {
            var environment = new FakeEnvironment { OnDelay = delegate(FakeEnvironment e) { e.Configuration.AutoConfigUrl = "http://pac.example/new.pac"; } };
            AssertPreserved(environment, "User's new PAC URL was ignored");
        });
        Check("changed bypass list during grace is preserved", delegate
        {
            var environment = new FakeEnvironment { OnDelay = delegate(FakeEnvironment e) { e.Configuration.Bypass = "<local>;campus.example"; } };
            AssertPreserved(environment, "User's new bypass rules were ignored");
        });
        Check("expired and future boot times are rejected", delegate
        {
            var expired = new FakeEnvironment();
            expired.Session.BootUtc = expired.Now.AddMinutes(-5);
            AssertPreserved(expired, "Late launch changed proxy");
            Assert(expired.Reads == 0, "Late launch read proxy");
            var future = new FakeEnvironment();
            future.Session.BootUtc = future.Now.AddSeconds(1);
            AssertPreserved(future, "Future boot time changed proxy");
        });
        Check("an initial check begun before five minutes can finish afterwards", delegate
        {
            var environment = new FakeEnvironment();
            environment.Session.BootUtc = environment.Now.AddMinutes(-5).AddSeconds(3);
            Assert(StartupProxyRepair.Run(true, environment) && environment.PortalProbes == 3,
                "Five-minute cutoff incorrectly interrupted an eligible initial check");
        });
        Check("the initial check cannot extend past one minute into later use", delegate
        {
            var environment = new FakeEnvironment { OnDelay = delegate(FakeEnvironment e) { e.Now = e.Now.AddSeconds(60); } };
            AssertPreserved(environment, "A prolonged initial check modified the proxy");
            Assert(environment.PortalProbes == 1, "Expired initial check continued to probe portals");
        });
        Check("only recognized cold and fast startup records open a boot window", delegate
        {
            DateTime now = DateTime.UtcNow;
            Assert(StartupSession.FromBootRecord(now, 0, 12) != null, "Cold boot rejected");
            Assert(StartupSession.FromBootRecord(now, 1, 12) != null, "Fast startup rejected");
            Assert(StartupSession.FromBootRecord(now, 2, 12) == null, "Hibernation resume treated as a fresh boot");
            Assert(StartupSession.FromBootRecord(now, 3, 12) == null, "Unknown boot type accepted");
            Assert(StartupSession.FromBootRecord(now, 0, 0) == null, "Invalid event record accepted");
            Assert(StartupSession.FromBootRecord(now, 0, 12).Identity == StartupSession.FromBootRecord(now, 0, 12).Identity,
                "A single boot was assigned multiple identities");
        });
        Check("changed address after the final probe is preserved", delegate
        {
            var environment = new FakeEnvironment { OnProbe = delegate(FakeEnvironment e) { if (e.Probes == 4) e.Configuration.Server = "127.0.0.1:7900"; } };
            AssertPreserved(environment, "Proxy changed just before commit was overwritten");
        });
        Check("xxyun starting after the final probe is preserved", delegate
        {
            var environment = new FakeEnvironment { OnProbe = delegate(FakeEnvironment e) { if (e.Probes == 6) e.Status = ProxyEndpointStatus.Listening; } };
            AssertPreserved(environment, "xxyun started just before commit was disrupted");
        });
        Check("proxy read failure consumes the check and never writes", delegate
        {
            var environment = new FakeEnvironment { ReadThrows = true };
            AssertPreserved(environment, "Read error changed proxy");
            environment.ReadThrows = false;
            AssertPreserved(environment, "Failed startup check was retried at runtime");
        });
        Check("stopping during grace cancels recovery", delegate
        {
            var environment = new FakeEnvironment { OnDelay = delegate(FakeEnvironment e) { e.Cancelled = true; } };
            AssertPreserved(environment, "Stopped startup still changed proxy");
        });
        Check("proxy changes while a portal request is pending cancel recovery", delegate
        {
            var environment = new FakeEnvironment { OnPortal = delegate(FakeEnvironment e) { e.Configuration.Server = "127.0.0.1:7891"; } };
            AssertPreserved(environment, "Changed proxy was overwritten after a failed request");
        });
        Check("stopping while a portal request is pending cancels recovery", delegate
        {
            var environment = new FakeEnvironment { OnPortal = delegate(FakeEnvironment e) { e.Cancelled = true; } };
            AssertPreserved(environment, "Stopped process continued after a failed portal request");
        });
        Check("portal probe request uses Windows proxy without sending a request", delegate
        {
            HttpWebRequest request = PortalClient.MakeSystemProxyProbeRequest("http://campus.invalid/");
            Assert(request.Proxy != null, "Startup portal probe did not use Windows proxy settings");
            Assert(request.Method == "GET" && !request.AllowAutoRedirect, "Startup probe can submit credentials or follow redirects");
            Assert(request.Timeout == 5000 && request.ReadWriteTimeout == 5000, "Startup probe timeout is not bounded");
            request.Abort();
        });
        Check("only transport failures count and HTTP errors still preserve the proxy", delegate
        {
            Assert(PortalClient.ClassifyPortalProbeFailure(new WebException("fixture", WebExceptionStatus.ConnectFailure)) == PortalProbeStatus.Failed,
                "Connection failure not counted");
            Assert(PortalClient.ClassifyPortalProbeFailure(new WebException("fixture", WebExceptionStatus.Timeout)) == PortalProbeStatus.Failed,
                "Timeout not counted");
            Assert(PortalClient.ClassifyPortalProbeFailure(new WebException("fixture", WebExceptionStatus.RequestCanceled)) == PortalProbeStatus.Unknown,
                "Cancellation counted as a failure");
            Assert(PortalClient.ClassifyPortalProbeFailure(new WebException("fixture", WebExceptionStatus.TrustFailure)) == PortalProbeStatus.Unknown,
                "Security failure counted as a failure");
            using (var response = new FixtureWebResponse())
                Assert(PortalClient.ClassifyPortalProbeFailure(new WebException("fixture", null, WebExceptionStatus.ProtocolError, response)) == PortalProbeStatus.Reached,
                    "HTTP error response counted as a connection failure");
        });
        Check("an already cancelled native portal probe sends no network request", delegate
        {
            using (var stop = new ManualResetEvent(true))
                Assert(PortalClient.ProbePortalViaSystemProxy(stop) == PortalProbeStatus.Unknown, "Cancelled probe performed work");
        });
        Check("a listener starting while backup is written is preserved", delegate
        {
            var environment = new FakeEnvironment { OnBackup = delegate(FakeEnvironment e) { e.Status = ProxyEndpointStatus.Listening; } };
            AssertPreserved(environment, "Proxy started during backup was disrupted");
        });
        Check("a stop during backup cancels the final write", delegate
        {
            var environment = new FakeEnvironment { OnBackup = delegate(FakeEnvironment e) { e.Cancelled = true; } };
            AssertPreserved(environment, "Stopped process committed proxy changes");
        });
        Check("backup failure prevents Windows settings write", delegate
        {
            var environment = new FakeEnvironment { OnBackup = delegate(FakeEnvironment e) { throw new IOException("fixture backup failed"); } };
            AssertPreserved(environment, "Failed backup allowed a proxy write");
        });
        Check("replacement background can wait for the previous mutex owner", delegate
        {
            MethodInfo acquire = typeof(BackgroundHost).GetMethod("TryAcquireInstance", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(acquire != null, "Background replacement still exits immediately instead of waiting for the previous owner");
            using (var mutex = new Mutex(false))
            using (var owned = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                var holder = new Thread(delegate() { mutex.WaitOne(); owned.Set(); release.WaitOne(); mutex.ReleaseMutex(); });
                holder.IsBackground = true;
                holder.Start();
                Assert(owned.WaitOne(1000), "Fixture mutex owner did not start");
                try { Assert(!(bool)acquire.Invoke(null, new object[] { mutex, 20 }), "Contended mutex granted ownership"); }
                finally { release.Set(); holder.Join(1000); }
                Assert((bool)acquire.Invoke(null, new object[] { mutex, 1000 }), "Replacement could not acquire released mutex");
                mutex.ReleaseMutex();
            }
        });
        Check("real startup delay stops immediately on an isolated stop event", delegate
        {
            using (var stop = new ManualResetEvent(true))
            {
                var environment = new WindowsStartupProxyEnvironment(stop);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                environment.Delay(5000);
                Assert(elapsed.ElapsedMilliseconds < 1000 && environment.IsCancellationRequested(), "Startup cancellation still waits for the grace period");
            }
        });
        Check("backup serializer preserves the original settings in the isolated directory", delegate
        {
            var fixture = new ProxyConfiguration { Flags = 3, Server = "127.0.0.1:7890", Bypass = "<local>;campus.example", AutoConfigUrl = "http://pac.example/config.pac?x=1&y=2" };
            new WindowsStartupProxyEnvironment().SaveBackup(fixture);
            var document = new System.Xml.XmlDocument();
            document.XmlResolver = null;
            document.Load(Path.Combine(AppData.DirectoryPath, "startup-proxy-backup.xml"));
            Assert(document.SelectSingleNode("/StartupProxyBackup/Flags").InnerText == "3", "Backup flags do not represent original settings");
            Assert(document.SelectSingleNode("/StartupProxyBackup/ProxyServer").InnerText == "127.0.0.1:7890", "Backup server changed");
            Assert(document.SelectSingleNode("/StartupProxyBackup/ProxyBypass").InnerText == "<local>;campus.example", "Backup bypass rules lost");
            Assert(document.SelectSingleNode("/StartupProxyBackup/AutoConfigUrl").InnerText == "http://pac.example/config.pac?x=1&y=2", "Backup PAC URL lost");
        });
        Check("remote, mixed, malformed and PAC-only settings are preserved", delegate
        {
            foreach (string server in new[] { "proxy.example:8080", "192.168.1.1:8080", "http=127.0.0.1:7890;https=proxy.example:8080", "127.0.0.1:0", "127.0.0.1:65536", "127.0.0.1", "127.0.0.1:7890;", "http=127.0.0.1:7890;bad=127.0.0.1:7890" })
            {
                var environment = new FakeEnvironment();
                environment.Configuration.Server = server;
                AssertPreserved(environment, "Unsupported proxy " + server + " changed");
            }
            var pac = new FakeEnvironment();
            pac.Configuration.Flags = 5;
            AssertPreserved(pac, "PAC-only configuration changed");
        });
        Check("recovery preserves PAC flag and the server address", delegate
        {
            var environment = new FakeEnvironment();
            environment.Configuration.Flags = 7;
            Assert(StartupProxyRepair.Run(true, environment), "Local proxy with PAC not recovered");
            Assert(environment.Configuration.Flags == 13, "PAC flag was lost");
            Assert(environment.Configuration.Server == "127.0.0.1:7890", "Server address was lost");
        });
        Check("loopback parsing handles protocol maps and IPv6", delegate
        {
            List<ProxyEndpoint> endpoints;
            Assert(StartupProxyRepair.TryGetLocalEndpoints("http=127.0.0.1:7890;https=127.0.0.1:7890", out endpoints) && endpoints.Count == 1,
                "Shared local endpoint not recognized");
            Assert(StartupProxyRepair.TryGetLocalEndpoints("localhost:7890", out endpoints) && endpoints.Count == 2,
                "localhost must protect both IPv4 and IPv6 listeners");
            Assert(StartupProxyRepair.TryGetLocalEndpoints("[::1]:7890", out endpoints) && endpoints.Count == 1 && endpoints[0].Address.Equals(IPAddress.IPv6Loopback),
                "IPv6 loopback not recognized");
        });
        Check("real loopback listener is recognized without accessing the Internet", delegate
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var endpoint = new ProxyEndpoint { Address = IPAddress.Loopback, Port = port };
            var environment = new WindowsStartupProxyEnvironment();
            try { Assert(environment.Probe(endpoint) == ProxyEndpointStatus.Listening, "Listening local port classified offline"); }
            finally { listener.Stop(); }
            Assert(environment.Probe(endpoint) == ProxyEndpointStatus.Closed, "Closed local port not recognized");
        });
        Check("native read layouts and Windows boot event query work", delegate
        {
            Assert(Marshal.SizeOf(typeof(WindowsProxySettings.OptionValue)) == 8, "Wrong native union size");
            Assert(Marshal.SizeOf(typeof(WindowsProxySettings.Option)) == (IntPtr.Size == 8 ? 16 : 12), "Wrong proxy option alignment");
            Assert(Marshal.SizeOf(typeof(WindowsProxySettings.OptionList)) == (IntPtr.Size == 8 ? 32 : 20), "Wrong proxy list alignment");
            ProxyConfiguration configuration = WindowsProxySettings.Read();
            Assert(configuration.Server != null, "Native proxy read failed");
            StartupSession session = StartupSession.ReadCurrent();
            Assert(session != null && session.BootUtc <= DateTime.UtcNow && session.BootUtc.Year >= 2020,
                "Cannot determine Windows boot time safely");
            Console.WriteLine("READ-ONLY native flags=" + configuration.Flags + ", boot age minutes=" + (int)(DateTime.UtcNow - session.BootUtc).TotalMinutes);
        });
        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
