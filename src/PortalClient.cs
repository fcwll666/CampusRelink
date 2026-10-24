using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

internal static class PortalClient
{
    static readonly object Gate = new object();

    internal static string TryLogin()
    {
        if (!Monitor.TryEnter(Gate)) return "已有登录检测正在运行";
        try
        {
            PortalSettings settings;
            CredentialAccount credential;
            if (!AppData.LoadConnection(out settings, out credential)) return "请重新保存认证接口与账号密码后启用";
            return ConnectionCoordinator.Run(settings, credential, new WindowsConnectionEnvironment(settings));
        }
        catch { AppData.Log("连接流程异常；请检查配置与本地网络状态。"); return "连接未完成，请查看运行日志"; }
        finally { Monitor.Exit(Gate); }
    }

    sealed class WindowsConnectionEnvironment : IConnectionEnvironment
    {
        readonly PortalSettings settings;
        internal WindowsConnectionEnvironment(PortalSettings settings) { this.settings = settings; }
        public bool HasInternet() { return PortalClient.HasInternet(settings); }
        public bool ConnectWifi(string profile, bool force)
        {
            if (String.IsNullOrWhiteSpace(profile)) return true;
            if (force) WirelessControl.EnsureEnabled();
            var single = PortalSettings.Default();
            single.WifiProfile = profile;
            single.ForceConnect = force;
            WifiManager.EnsureConnected(single);
            return String.Equals(WifiManager.CurrentProfile(), profile, StringComparison.OrdinalIgnoreCase)
                && WifiManager.AddressReady();
        }
        public AuthenticationOutcome Authenticate(PortalSettings configuration, CredentialAccount account)
        { return Submit(configuration, account); }
        public void Delay(int milliseconds) { Thread.Sleep(milliseconds); }
        public void Log(string message) { AppData.Log(message); }
    }

    internal static AuthenticationOutcome Submit(PortalSettings settings, CredentialAccount account)
    {
        var cookies = new CookieContainer();
        try
        {
            if (!String.IsNullOrWhiteSpace(settings.PortalUrl))
            {
                try { Get(settings.PortalUrl, cookies); }
                catch { AppData.Log("门户首页预取未完成，继续尝试认证。"); }
            }
            HttpWebRequest request = MakeRequest(settings.LoginUrl, cookies);
            request.Method = "POST";
            request.ContentType = PortalProtocol.ContentType(settings);
            request.Accept = "application/json";
            byte[] data = Encoding.UTF8.GetBytes(PortalProtocol.BuildPayload(settings, account));
            request.ContentLength = data.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(data, 0, data.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300) return AuthenticationOutcome.Unknown;
                bool success;
                return PortalProtocol.TryReadSuccess(settings, ReadResponse(response), out success)
                    ? (success ? AuthenticationOutcome.Accepted : AuthenticationOutcome.Rejected)
                    : AuthenticationOutcome.Unknown;
            }
        }
        catch (WebException ex)
        {
            bool unreachable = ex.Response == null && (ex.Status == WebExceptionStatus.ConnectFailure
                || ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.Timeout
                || ex.Status == WebExceptionStatus.ReceiveFailure || ex.Status == WebExceptionStatus.SendFailure);
            if (ex.Response != null) ex.Response.Close();
            return unreachable ? AuthenticationOutcome.Unreachable : AuthenticationOutcome.Unknown;
        }
        catch { return AuthenticationOutcome.Unknown; }
    }

    internal static string GetStatus() { return HasInternet() ? "联网检测通过" : "联网检测未通过"; }
    internal static bool HasInternet() { return HasInternet(AppData.LoadSettings()); }
    internal static bool HasInternet(PortalSettings settings)
    {
        try
        {
            HttpWebRequest request = MakeRequest(settings.ConnectivityUrl, new CookieContainer());
            request.Timeout = 5000;
            request.ReadWriteTimeout = 5000;
            using (var response = (HttpWebResponse)request.GetResponse())
                return response.StatusCode == HttpStatusCode.OK
                    && IsExpectedConnectivityResponse(response.StatusCode, ReadResponse(response), settings.ConnectivityExpected);
        }
        catch { return false; }
    }

    internal static bool IsExpectedConnectivityResponse(HttpStatusCode status, string body, string expected)
    {
        return status == HttpStatusCode.OK && body != null && !String.IsNullOrWhiteSpace(expected)
            && String.Equals(body.Trim(), expected.Trim(), StringComparison.Ordinal);
    }

    internal static PortalProbeStatus ProbePortalViaSystemProxy(WaitHandle stop)
    {
        if (stop != null && stop.WaitOne(0)) return PortalProbeStatus.Unknown;
        PortalSettings settings = AppData.LoadSettings();
        // A login endpoint is not necessarily a GET-able portal. Do not infer a repair target.
        if (settings.Validate() != null || String.IsNullOrWhiteSpace(settings.PortalUrl)) return PortalProbeStatus.Unknown;
        RegisteredWaitHandle cancellation = null;
        try
        {
            HttpWebRequest request = MakeSystemProxyProbeRequest(settings.PortalUrl);
            if (stop != null) cancellation = ThreadPool.RegisterWaitForSingleObject(stop,
                delegate { try { request.Abort(); } catch { } }, null, Timeout.Infinite, true);
            using (var response = (HttpWebResponse)request.GetResponse()) return PortalProbeStatus.Reached;
        }
        catch (WebException ex)
        {
            try { return ClassifyPortalProbeFailure(ex); }
            finally { if (ex.Response != null) ex.Response.Close(); }
        }
        catch { return PortalProbeStatus.Unknown; }
        finally { if (cancellation != null) cancellation.Unregister(null); }
    }

    internal static HttpWebRequest MakeSystemProxyProbeRequest(string url)
    {
        HttpWebRequest request = MakeRequest(url, new CookieContainer());
        request.Proxy = WebRequest.GetSystemWebProxy();
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        return request;
    }
    internal static PortalProbeStatus ClassifyPortalProbeFailure(WebException error)
    {
        if (error.Response != null) return PortalProbeStatus.Reached;
        switch (error.Status)
        {
            case WebExceptionStatus.ConnectFailure:
            case WebExceptionStatus.NameResolutionFailure:
            case WebExceptionStatus.ProxyNameResolutionFailure:
            case WebExceptionStatus.Timeout:
            case WebExceptionStatus.ReceiveFailure:
            case WebExceptionStatus.SendFailure: return PortalProbeStatus.Failed;
            default: return PortalProbeStatus.Unknown;
        }
    }

    static string Get(string url, CookieContainer cookies)
    {
        using (var response = (HttpWebResponse)MakeRequest(url, cookies).GetResponse())
            return ReadResponse(response);
    }
    static string ReadResponse(HttpWebResponse response)
    {
        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
        {
            var text = new StringBuilder();
            var buffer = new char[2048];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length + count > 65536) throw new InvalidDataException("Response too large");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
    }
    static HttpWebRequest MakeRequest(string url, CookieContainer cookies)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Proxy = null;
        request.Timeout = 10000;
        request.ReadWriteTimeout = 10000;
        request.AllowAutoRedirect = false;
        request.CookieContainer = cookies;
        request.UserAgent = "CampusRelink/1.0.3";
        request.Method = "GET";
        return request;
    }
    internal static string Shorten(string text)
    {
        string flat = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > 200 ? flat.Substring(0, 200) + "..." : flat;
    }
}
