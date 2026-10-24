using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

internal sealed class ProxyConfiguration
{
    internal uint Flags;
    internal string Server;
    internal string Bypass;
    internal string AutoConfigUrl;

    internal bool Matches(ProxyConfiguration other)
    {
        return other != null && Flags == other.Flags && String.Equals(Server, other.Server, StringComparison.Ordinal)
            && String.Equals(Bypass, other.Bypass, StringComparison.Ordinal)
            && String.Equals(AutoConfigUrl, other.AutoConfigUrl, StringComparison.Ordinal);
    }
}

internal sealed class ProxyEndpoint
{
    internal IPAddress Address;
    internal int Port;
}

internal enum ProxyEndpointStatus { Closed, Listening, Unknown }
internal enum PortalProbeStatus { Failed, Reached, Unknown }

// Windows 与时钟/等待位于边界，离线测试使用隔离环境，不写真实代理。
internal interface IStartupProxyEnvironment
{
    bool TryClaimStartupCheck();
    bool IsCancellationRequested();
    bool IsWithinStartupWindow();
    ProxyConfiguration ReadProxy();
    PortalProbeStatus ProbePortal();
    ProxyEndpointStatus Probe(ProxyEndpoint endpoint);
    void Delay(int milliseconds);
    void SaveBackup(ProxyConfiguration expected);
    bool ApplyRecovery(ProxyConfiguration expected);
    void Log(string message);
}

internal static class StartupProxyRepair
{
    internal static bool IsStartupLaunch(string[] args)
    {
        // 应用内启用/重启必须显式标为运行期。旧自启只有 --background，仍受开机时间限制。
        return args != null && args.Length > 0 && args[0] == "--background"
            && Array.IndexOf(args, "--runtime") < 0;
    }

    internal static uint RecoveryFlags(uint flags)
    {
        // 只关闭手动代理、开启直连与自动检测。保留 PAC 等其他标志。
        return (flags & ~2u) | 1u | 8u;
    }

    internal static bool Run(bool startupLaunch, IStartupProxyEnvironment environment)
    {
        if (!startupLaunch) return false;
        try
        {
            // 即使本次没有代理/代理正常，也消耗本次开机的检查机会；之后不再尝试。
            if (environment.IsCancellationRequested() || !environment.TryClaimStartupCheck()) return false;
            ProxyConfiguration original = environment.ReadProxy();
            List<ProxyEndpoint> endpoints;
            if ((original.Flags & 2u) == 0 || !TryGetLocalEndpoints(original.Server, out endpoints))
            {
                environment.Log("开机代理检查：没有需要修复的本机手动代理。");
                return false;
            }

            // 只统计本次启动的前三次系统代理门户请求。任一次有响应或不确定都不修改代理。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!CanRepair(original, endpoints, environment)) return false;
                PortalProbeStatus portal = environment.ProbePortal();
                if (!CanRepair(original, endpoints, environment)) return false;
                if (portal != PortalProbeStatus.Failed)
                {
                    environment.Log(portal == PortalProbeStatus.Reached
                        ? "开机代理检查：通过当前代理设置可以访问门户，保留代理。"
                        : "开机代理检查：门户结果无法确认，保留代理。");
                    return false;
                }
                environment.Log("开机门户检测第 " + (attempt + 1) + "/3 次连接失败，本机代理端口没有监听。");
                if (attempt < 2) environment.Delay(5000);
            }

            // 最后一轮检查与备份写入中也可能发生启动/停止/设置变化，前后都完整复核。
            if (!CanRepair(original, endpoints, environment)) return false;
            environment.SaveBackup(original);
            if (!CanRepair(original, endpoints, environment)) return false;
            if (environment.ApplyRecovery(original))
            {
                environment.Log("开机代理修复：前三次门户连接均失败且本机代理端口持续无监听，已关闭手动代理并恢复自动检测；代理地址保留。运行期不会修改代理。");
                return true;
            }
            environment.Log("开机代理检查：修复前状态发生变化，保留代理。");
        }
        catch (Exception ex)
        {
            // 获取时间/设置、写备份或调用系统 API 失败时，不扩大修复范围，也不阻止直连认证。
            environment.Log("开机代理检查未完成：" + ex.Message + "。继续直连认证，运行期不再修改代理。");
        }
        return false;
    }

    static bool CanRepair(ProxyConfiguration original, List<ProxyEndpoint> endpoints, IStartupProxyEnvironment environment)
    {
        if (environment.IsCancellationRequested())
        {
            environment.Log("开机代理检查：后台已停止，取消检查并保留代理。");
            return false;
        }
        if (!environment.IsWithinStartupWindow() || !original.Matches(environment.ReadProxy()))
        {
            environment.Log("开机代理检查：已超出启动阶段或设置已变化，保留代理。");
            return false;
        }
        foreach (ProxyEndpoint endpoint in endpoints)
        {
            if (environment.Probe(endpoint) != ProxyEndpointStatus.Closed)
            {
                environment.Log("开机代理检查：代理正在监听或状态无法确认，保留代理。");
                return false;
            }
        }
        return true;
    }

    internal static bool TryGetLocalEndpoints(string server, out List<ProxyEndpoint> endpoints)
    {
        endpoints = new List<ProxyEndpoint>();
        if (String.IsNullOrWhiteSpace(server)) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in server.Split(';'))
        {
            string address = part.Trim();
            int equals = address.IndexOf('=');
            if (equals >= 0)
            {
                string protocol = address.Substring(0, equals).Trim();
                if (!Regex.IsMatch(protocol, "^(http|https|socks|ftp)$", RegexOptions.IgnoreCase)) return false;
                address = address.Substring(equals + 1).Trim();
            }
            Match match = Regex.Match(address,
                @"^(?:(?:https?|socks[45]?)://)?(?<host>\[[^\]]+\]|[^:/\s]+):(?<port>\d{1,5})$", RegexOptions.IgnoreCase);
            int port;
            if (!match.Success || !Int32.TryParse(match.Groups["port"].Value, out port) || port < 1 || port > 65535) return false;
            string host = match.Groups["host"].Value.Trim('[', ']');
            if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                AddEndpoint(endpoints, seen, IPAddress.Loopback, port);
                AddEndpoint(endpoints, seen, IPAddress.IPv6Loopback, port);
            }
            else
            {
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip) || !IPAddress.IsLoopback(ip)) return false;
                AddEndpoint(endpoints, seen, ip, port);
            }
        }
        return endpoints.Count > 0;
    }

    static void AddEndpoint(List<ProxyEndpoint> endpoints, HashSet<string> seen, IPAddress address, int port)
    {
        if (seen.Add(address.ToString() + ":" + port)) endpoints.Add(new ProxyEndpoint { Address = address, Port = port });
    }
}

internal sealed class StartupSession
{
    // 五分钟只判断本轮开始时间；本轮最多持续一分钟，绝不延伸到运行期重连。
    internal const int WindowMinutes = 5;
    internal const int MaximumCheckSeconds = 60;
    internal DateTime BootUtc;
    internal DateTime? ConnectionStartedUtc;
    internal string Identity;

    internal bool IsWithinWindow(DateTime nowUtc)
    {
        TimeSpan elapsed = nowUtc - BootUtc;
        return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(WindowMinutes);
    }

    internal bool IsCheckActive(DateTime nowUtc)
    {
        if (!ConnectionStartedUtc.HasValue) return false;
        TimeSpan elapsed = nowUtc - ConnectionStartedUtc.Value;
        return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(MaximumCheckSeconds);
    }

    internal bool TryClaim(string dataDirectory, DateTime nowUtc)
    {
        if (String.IsNullOrEmpty(Identity) || !IsWithinWindow(nowUtc)) return false;
        Directory.CreateDirectory(dataDirectory);
        string marker = Path.Combine(dataDirectory, "startup-proxy-check.txt");
        using (var stream = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true))
                if (reader.ReadToEnd().Trim() == Identity) return false;
            stream.Position = 0;
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true)) writer.Write(Identity);
            stream.Flush(true);
            ConnectionStartedUtc = nowUtc;
            return true;
        }
    }

    internal static StartupSession FromBootRecord(DateTime createdUtc, int bootType, long recordId)
    {
        // 0=冷启动、1=快速启动；2=休眠恢复，属于使用中恢复，不开放新的修复机会。
        if ((bootType != 0 && bootType != 1) || recordId <= 0) return null;
        return new StartupSession
        {
            BootUtc = createdUtc,
            Identity = "boot-" + recordId.ToString(CultureInfo.InvariantCulture) + "-" + createdUtc.Ticks.ToString("x16", CultureInfo.InvariantCulture)
        };
    }

    internal static StartupSession ReadCurrent()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=27)]]") { ReverseDirection = true };
            using (var reader = new EventLogReader(query))
            using (EventRecord record = reader.ReadEvent())
            {
                if (record == null || !record.TimeCreated.HasValue || !record.RecordId.HasValue || record.Properties.Count == 0) return null;
                return FromBootRecord(record.TimeCreated.Value.ToUniversalTime(),
                    Convert.ToInt32(record.Properties[0].Value, CultureInfo.InvariantCulture), record.RecordId.Value);
            }
        }
        // 无法确认真实开机记录时仅继续直连认证，不用登录时间冒充开机时间。
        catch { return null; }
    }
}

internal sealed class WindowsStartupProxyEnvironment : IStartupProxyEnvironment
{
    readonly StartupSession session = StartupSession.ReadCurrent();
    readonly WaitHandle stop;

    internal WindowsStartupProxyEnvironment() { }
    internal WindowsStartupProxyEnvironment(WaitHandle stop) { this.stop = stop; }

    public bool IsCancellationRequested() { return stop != null && stop.WaitOne(0); }
    public bool IsWithinStartupWindow() { return session != null && session.IsCheckActive(DateTime.UtcNow); }
    public bool TryClaimStartupCheck()
    {
        if (IsCancellationRequested()) return false;
        if (session == null)
        {
            Log("开机代理检查：无法确认真实开机时间，保留代理。");
            return false;
        }
        if (!session.IsWithinWindow(DateTime.UtcNow))
        {
            Log("开机代理检查：首次连接已超过本次开机前 5 分钟，保留代理。");
            return false;
        }
        return session.TryClaim(AppData.DirectoryPath, DateTime.UtcNow);
    }
    public ProxyConfiguration ReadProxy() { return WindowsProxySettings.Read(); }
    public void Delay(int milliseconds)
    {
        if (stop != null) stop.WaitOne(milliseconds);
        else Thread.Sleep(milliseconds);
    }
    public void Log(string message) { AppData.Log(message); }

    public PortalProbeStatus ProbePortal() { return PortalClient.ProbePortalViaSystemProxy(stop); }

    public ProxyEndpointStatus Probe(ProxyEndpoint endpoint)
    {
        try
        {
            // 只读操作系统监听表，不向代理发送连接。Windows 拒绝本机连接有时要等约 2 秒，
            // 用短连接超时会把明确的关闭状态误判成未知，也可能打扰正在启动的代理。
            foreach (IPEndPoint listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (listener.Port != endpoint.Port) continue;
                // IPv6 通配监听可能同时接收 IPv4，无法确认时按「正在监听」保留代理。
                if (listener.Address.Equals(endpoint.Address) || listener.Address.Equals(IPAddress.IPv6Any)
                    || (listener.Address.Equals(IPAddress.Any) && endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                    return ProxyEndpointStatus.Listening;
            }
            return ProxyEndpointStatus.Closed;
        }
        catch { return ProxyEndpointStatus.Unknown; }
    }

    public void SaveBackup(ProxyConfiguration expected)
    {
        Directory.CreateDirectory(AppData.DirectoryPath);
        string backup = Path.Combine(AppData.DirectoryPath, "startup-proxy-backup.xml");
        using (XmlWriter writer = XmlWriter.Create(backup, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            writer.WriteStartElement("StartupProxyBackup");
            writer.WriteElementString("SavedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            writer.WriteElementString("Flags", expected.Flags.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("ProxyServer", expected.Server);
            writer.WriteElementString("ProxyBypass", expected.Bypass ?? "");
            writer.WriteElementString("AutoConfigUrl", expected.AutoConfigUrl ?? "");
            writer.WriteEndElement();
        }
    }

    public bool ApplyRecovery(ProxyConfiguration expected)
    {
        // 系统写入边界再复核一次；控制器已保证备份成功且备份后完整复核。
        if (IsCancellationRequested() || !IsWithinStartupWindow() || !expected.Matches(ReadProxy())) return false;
        List<ProxyEndpoint> endpoints;
        if (!StartupProxyRepair.TryGetLocalEndpoints(expected.Server, out endpoints)) return false;
        foreach (ProxyEndpoint endpoint in endpoints) if (Probe(endpoint) != ProxyEndpointStatus.Closed) return false;
        if (IsCancellationRequested()) return false;
        WindowsProxySettings.SetFlags(StartupProxyRepair.RecoveryFlags(expected.Flags));
        return true;
    }
}

internal static class WindowsProxySettings
{
    // 按微软建议查询 FLAGS_UI（10），写回 FLAGS（1）。不直接编辑 DefaultConnectionSettings 字节。
    // https://learn.microsoft.com/en-us/windows/win32/api/wininet/ns-wininet-internet_per_conn_optionw
    [StructLayout(LayoutKind.Explicit)]
    internal struct OptionValue
    {
        [FieldOffset(0)] internal uint Number;
        [FieldOffset(0)] internal IntPtr Text;
        [FieldOffset(0)] internal System.Runtime.InteropServices.ComTypes.FILETIME Time;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Option { internal uint Id; internal OptionValue Value; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct OptionList
    {
        internal uint Size;
        internal IntPtr Connection;
        internal uint Count, Error;
        internal IntPtr Options;
    }
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InternetQueryOption(IntPtr internet, uint option, ref OptionList list, ref uint size);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InternetSetOption(IntPtr internet, uint option, ref OptionList list, uint size);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InternetSetOptionNoBuffer(IntPtr internet, uint option, IntPtr buffer, uint size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);

    internal static ProxyConfiguration Read()
    {
        try { return Query(10); }
        catch (Win32Exception) { return Query(1); }
    }

    static ProxyConfiguration Query(uint flagOption)
    {
        int optionSize = Marshal.SizeOf(typeof(Option));
        uint[] ids = { flagOption, 2, 3, 4 };
        IntPtr options = Marshal.AllocHGlobal(optionSize * ids.Length);
        for (int i = 0; i < ids.Length; i++) Marshal.StructureToPtr(new Option { Id = ids[i] }, IntPtr.Add(options, optionSize * i), false);
        try
        {
            var list = new OptionList { Size = (uint)Marshal.SizeOf(typeof(OptionList)), Count = (uint)ids.Length, Options = options };
            uint size = list.Size;
            if (!InternetQueryOption(IntPtr.Zero, 75, ref list, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var values = new Option[ids.Length];
            for (int i = 0; i < values.Length; i++) values[i] = (Option)Marshal.PtrToStructure(IntPtr.Add(options, optionSize * i), typeof(Option));
            return new ProxyConfiguration
            {
                Flags = values[0].Value.Number,
                Server = ReadString(values[1].Value.Text),
                Bypass = ReadString(values[2].Value.Text),
                AutoConfigUrl = ReadString(values[3].Value.Text)
            };
        }
        finally
        {
            for (int i = 1; i < ids.Length; i++)
            {
                var value = (Option)Marshal.PtrToStructure(IntPtr.Add(options, optionSize * i), typeof(Option));
                if (value.Value.Text != IntPtr.Zero) GlobalFree(value.Value.Text);
            }
            Marshal.FreeHGlobal(options);
        }
    }

    static string ReadString(IntPtr value) { return value == IntPtr.Zero ? "" : Marshal.PtrToStringUni(value); }

    internal static void SetFlags(uint flags)
    {
        IntPtr options = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Option)));
        try
        {
            Marshal.StructureToPtr(new Option { Id = 1, Value = new OptionValue { Number = flags } }, options, false);
            var list = new OptionList { Size = (uint)Marshal.SizeOf(typeof(OptionList)), Count = 1, Options = options };
            if (!InternetSetOption(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            bool changed = InternetSetOptionNoBuffer(IntPtr.Zero, 39, IntPtr.Zero, 0);
            bool refreshed = InternetSetOptionNoBuffer(IntPtr.Zero, 37, IntPtr.Zero, 0);
            if (!changed || !refreshed) AppData.Log("代理选项已更新，但系统刷新通知失败；必要时重新打开相关应用。");
        }
        finally { Marshal.FreeHGlobal(options); }
    }
}
