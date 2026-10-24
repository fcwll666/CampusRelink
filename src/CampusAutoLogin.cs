using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

[assembly: AssemblyTitle("CampusRelink")]
[assembly: AssemblyProduct("CampusRelink")]
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]

static class WifiManager
{
    internal const int ProfileWaitSeconds = 12;
    const int AddressWaitSeconds = 6;
    static DateTime lastAttempt = DateTime.MinValue;
    sealed class LinkState
    {
        internal bool HasInterface;
        internal bool Connected;
        internal string Profile;
    }
    internal static void EnsureConnected(PortalSettings settings)
    {
        string target = settings.WifiProfile ?? "";
        if (target.Length == 0) return;
        try
        {
            LinkState state = Inspect();
            if (!state.HasInterface) { AppData.Log("未检测到无线网卡，跳过无线连接。"); return; }
            if (state.Connected && String.Equals(state.Profile, target, StringComparison.OrdinalIgnoreCase) && AddressReady()) return;
            if ((DateTime.UtcNow - lastAttempt).TotalSeconds < 15) return;
            lastAttempt = DateTime.UtcNow;
            if (!ProfileExists(target)) { AppData.Log("指定的无线配置尚未保存，请先在 Windows 中连接并保存该网络。"); return; }
            AppData.Log("正在连接配置的无线网络。");
            AppData.Log(ConnectOnce(target, settings.ForceConnect) ? "无线网络与地址已就绪。" : "无线连接暂未成功。");
        }
        catch { AppData.Log("无线连接未完成，请检查 Windows 无线配置。"); }
    }

    // 发起一次连接并等它真的关联上。netsh 只要请求发出就返回成功，
    // 「目标网络不在覆盖范围内」这种情况必须靠轮询 SSID 才能发现。
    static bool ConnectOnce(string profile, bool allowRadioWake)
    {
        string output = RunNetsh(ConnectionArguments(profile));
        // 「接口电源关闭」是无线电被关掉时的特征。强连接模式下补一次开无线电再重试——比事先判断
        // 更可靠：radio 被外部关掉后，软件开关常常很快被系统标回 ON，光看开关会以为一切正常，
        // 而 netsh 这边照样连不上。
        // 没勾强连接模式时不做这件事：那种模式下程序不碰 WLAN 开关，只维持已联网的连接。
        if (allowRadioWake && IsRadioOff(output))
        {
            AppData.Log("无线接口电源关闭，尝试打开无线电后重试连接。");
            if (WirelessControl.SetRadioOn())
            {
                Thread.Sleep(2000);
            output = RunNetsh(ConnectionArguments(profile));
            }
        }
        if (LooksDenied(output))
        {
            AppData.Log("netsh 拒绝连接请求：" + PortalClient.Shorten(output));
            return false;
        }

        bool linked = false;
        for (int i = 0; i < ProfileWaitSeconds && !linked; i++)
        {
            Thread.Sleep(1000);
            LinkState state = Inspect();
            linked = state.Connected && String.Equals(state.Profile, profile, StringComparison.OrdinalIgnoreCase);
        }
        if (!linked)
        {
            AppData.Log(profile + " 连接失败（" + ProfileWaitSeconds + " 秒内未关联到该网络）。");
            return false;
        }

        for (int i = 0; i < AddressWaitSeconds; i++)
        {
            if (AddressReady()) { AppData.Log("已连接 " + profile + "，网络地址就绪。"); return true; }
            Thread.Sleep(1000);
        }
        AppData.Log(profile + " 已关联但没有拿到网络地址。");
        return false;
    }

    internal static string CurrentProfile()
    {
        try { return Inspect().Profile; }
        catch { return null; }
    }

    internal static bool HasWirelessInterface()
    {
        try { return Inspect().HasInterface; }
        catch { return false; }
    }

    // 列出系统里已保存的无线配置，供界面下拉选择。
    internal static List<string> SavedProfiles()
    {
        var list = new List<string>();
        try
        {
            string output = RunNetsh("wlan show profiles");
            foreach (Match m in Regex.Matches(output ?? "", "(?m)^\\s*(?:All User Profile|所有用户配置文件)\\s*:\\s*(.+?)\\s*$"))
            {
                string name = m.Groups[1].Value.Trim();
                if (name.Length == 0 || name == "<None>" || name == "<无>") continue;
                if (!list.Contains(name)) list.Add(name);
            }
        }
        catch (Exception ex) { AppData.Log("读取无线配置列表失败：" + ex.Message); }
        return list;
    }

    // netsh wlan show interfaces 在非管理员下可能直接报权限错误，此时当作「没有无线接口」处理。
    static LinkState Inspect()
    {
        var state = new LinkState();
        // Native profile identity is exact, including intentional spaces.
        // Text-formatted netsh output cannot unambiguously represent that identity.
        var native = new WindowsWifiProfileSource().Read();
        if (!String.IsNullOrEmpty(native.ConnectedProfile))
        {
            state.HasInterface = state.Connected = true;
            state.Profile = native.ConnectedProfile;
            return state;
        }
        string output = RunNetsh("wlan show interfaces");
        if (String.IsNullOrEmpty(output)) return state;
        if (!Regex.IsMatch(output, "^[ \\t]*(?:Name|名称)[ \\t]*:", RegexOptions.Multiline)) return state;

        state.HasInterface = true;
        state.Profile = ParseProfile(output);

        Match condition = Regex.Match(output, "^[ \\t]*(?:State|状态)[ \\t]*:[ \\t]*(.+?)[ \\t]*\\r?$", RegexOptions.Multiline);
        state.Connected = condition.Success && IsConnected(condition.Groups[1].Value);
        return state;
    }

    internal static string ParseProfile(string output)
    {
        Match profile = Regex.Match(output ?? "", @"^[ \t]*(?:Profile|配置文件)[ \t]*:[ \t]*(.+?)[ \t]*\r?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return profile.Success ? profile.Groups[1].Value.Trim() : null;
    }

    // 中文界面是「已连接」，英文界面是「connected」——注意 disconnected 也含 connected 子串。
    static bool IsConnected(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0) return false;
        if (text.StartsWith("已", StringComparison.Ordinal)) return text.StartsWith("已连接", StringComparison.Ordinal);
        return text.StartsWith("connected", StringComparison.OrdinalIgnoreCase);
    }

    // 无线接口是否已经拿到可用的 IPv4 地址。169.254.x.x 是 DHCP 失败后的自动私有地址，不算就绪。
    internal static bool AddressReady()
    {
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    static bool ProfileExists(string profile)
    {
        var native = new WindowsWifiProfileSource().Read();
        if (WifiProfileChoices.Find(native.Profiles, profile) != null) return true;
        if (native.QuerySucceeded) return false;
        string output = RunNetsh("wlan show profiles");
        return Regex.IsMatch(output ?? "", ":[ \\t]*" + Regex.Escape(profile) + "[ \\t]*\\r?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }

    static bool LooksDenied(string output)
    {
        return Regex.IsMatch(output ?? "", "error|错误|拒绝|无法|未能|denied|elevation|failed",
            RegexOptions.IgnoreCase);
    }

    // netsh 在无线电台关闭时的两种报错：0x80342002（连接请求失败）与 2150899714（接口电源关闭）。
    static bool IsRadioOff(string output)
    {
        string text = output ?? "";
        return text.IndexOf("电源关闭", StringComparison.Ordinal) >= 0
            || text.IndexOf("powered off", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("0x80342002", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("2150899714", StringComparison.Ordinal) >= 0;
    }

    internal static string ConnectionArguments(string profile)
    {
        if (String.IsNullOrWhiteSpace(profile)) throw new ArgumentException("请选择已保存的 Wi-Fi 配置。");
        foreach (char character in profile)
            if (character == '"' || Char.IsControl(character) || character == '\u2028' || character == '\u2029')
                throw new ArgumentException("无线配置名包含无法提交的字符。");
        return "wlan connect name=\"" + profile + "\"";
    }

    internal static string RunNetsh(string arguments)
    {
        var info = new ProcessStartInfo("netsh.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (Process process = Process.Start(info))
        {
            byte[] raw;
            using (var buffer = new MemoryStream())
            {
                process.StandardOutput.BaseStream.CopyTo(buffer);
                process.StandardError.ReadToEnd();
                raw = buffer.ToArray();
            }
            if (!process.WaitForExit(8000)) { try { process.Kill(); } catch { } }
            return DecodeNetsh(raw);
        }
    }

    // netsh 输出的是 UTF-8（实测 Windows 11 中文版），而 Process 默认按系统 ANSI 代码页解码，
    // 「状态」「名称」这类关键词会整段变成乱码，解析随之失效（SSID 是 ASCII 才侥幸没暴露）。
    // 这里取回原始字节自行判定：能被严格 UTF-8 解出来就用 UTF-8，否则退回系统代码页。
    static string DecodeNetsh(byte[] raw)
    {
        if (raw == null || raw.Length == 0) return "";
        try { return new UTF8Encoding(false, true).GetString(raw); }
        catch (ArgumentException) { return Encoding.Default.GetString(raw); }
    }
}

// 强连接模式：无线被关掉时把它打开再谈连接。
//
// 两条路径分开走——软件无线电开关（等同于系统设置里的 WLAN 开关）用 WLAN API，普通权限就能改；
// 适配器被禁用是另一种状态，只能靠 netsh，那一步需要管理员，失败时只记日志、不阻塞流程。
static class WirelessControl
{
    const int OpcodeRadioState = 4;     // wlan_intf_opcode_radio_state
    const int PhyRadioStateSize = 12;   // DWORD + 两个枚举

    [DllImport("wlanapi.dll")]
    static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")]
    static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")]
    static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out int dataSize, out IntPtr data, out int opcodeValueType);
    [DllImport("wlanapi.dll")]
    static extern int WlanSetInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, int dataSize,
        IntPtr data, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    static extern void WlanFreeMemory(IntPtr memory);

    internal static void EnsureEnabled()
    {
        // WlanSvc 停掉时 netsh wlan 什么都看不到，连接口都列不出来，得先把它拉起来。
        if (!ServiceRunning())
        {
            AppData.Log("WLAN 服务未在运行，尝试启动（这一步需要管理员权限）。");
            RunTool("sc.exe", "start wlansvc");
            for (int i = 0; i < 10 && !ServiceRunning(); i++) Thread.Sleep(500);
            AppData.Log(ServiceRunning() ? "WLAN 服务已启动。" : "WLAN 服务启动失败，后续步骤可能无效。");
        }

        // 注意：radio 关掉时 netsh wlan show interfaces 依然会列出接口，
        // 所以不能拿「接口是否存在」当判断依据，必须查无线电开关本身。
        bool? radioOn = QuerySoftwareRadioOn();
        if (radioOn == true) return;

        AppData.Log("强连接模式：WLAN 无线电处于关闭状态，尝试打开它。");

        if (!AdapterEnabled())
        {
            AppData.Log("无线适配器处于禁用状态，尝试启用（这一步需要管理员权限）。");
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 || adapter.Name.IndexOf('"') >= 0) continue;
                string output = WifiManager.RunNetsh("interface set interface name=\"" + adapter.Name + "\" admin=enable");
                if (LooksLikeFailure(output)) AppData.Log("启用无线适配器未成功，可能需要管理员权限。");
            }
        }

        if (SetRadioOn()) AppData.Log("已通过 WLAN API 打开无线电开关。");

        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(500);
            if (QuerySoftwareRadioOn() == true) break;
        }
        AppData.Log(QuerySoftwareRadioOn() == true
            ? "无线已就绪。"
            : "无线电仍未能打开，请检查飞行模式或机身上的物理开关。");
    }

    // 供自检输出，便于确认判断依据来自哪里。
    internal static string DescribeRadio()
    {
        bool? on = QuerySoftwareRadioOn();
        if (on == null) return "无法读取（WLAN API 不可用）";
        return on.Value ? "已开启" : "已关闭";
    }

    // null 表示查不出来。只看软件开关——硬件开关是只读位，飞行模式打开时 API 改不动。
    static bool? QuerySoftwareRadioOn()
    {
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero, data = IntPtr.Zero;
        try
        {
            int negotiated, size, valueType;
            if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0) return null;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return null;
            if (Marshal.ReadInt32(list) < 1) return null;

            Guid guid = (Guid)Marshal.PtrToStructure(new IntPtr(list.ToInt64() + 8), typeof(Guid));
            if (WlanQueryInterface(handle, ref guid, OpcodeRadioState, IntPtr.Zero, out size, out data, out valueType) != 0) return null;

            int phys = Marshal.ReadInt32(data);
            if (phys <= 0 || phys > 64) return null;

            bool allOn = true, hardwareOff = false;
            for (int i = 0; i < phys; i++)
            {
                IntPtr phy = new IntPtr(data.ToInt64() + 4 + i * PhyRadioStateSize);
                if (Marshal.ReadInt32(phy, 4) == 0) allOn = false;       // dot11SoftwareRadioState
                if (Marshal.ReadInt32(phy, 8) == 0) hardwareOff = true;  // dot11HardwareRadioState
            }
            if (hardwareOff) AppData.Log("硬件无线电开关处于关闭状态（飞行模式或机身开关），API 无法代劳。");
            return allOn;
        }
        catch { return null; }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    // 用 sc query 查服务本身的运行状态。不能拿「netsh wlan 有没有输出」来判断：
    // 适配器被禁用时 netsh 同样什么都看不到，但那是适配器的问题、不是服务没跑，
    // 按前者判断会误报，还会多做一次注定失败的启动尝试。
    static bool ServiceRunning()
    {
        try
        {
            var info = new ProcessStartInfo("sc.exe", "query wlansvc")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(8000)) { try { process.Kill(); } catch { } }
                return Regex.IsMatch(output ?? "", "RUNNING");
            }
        }
        catch { return true; }   // 查不出来就当它在跑，免得误报
    }

    static void RunTool(string file, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(info))
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(8000)) { try { process.Kill(); } catch { } }
            }
        }
        catch (Exception ex) { AppData.Log("执行 " + file + " 失败：" + ex.Message); }
    }

    // netsh interface show interface 每行是：管理状态 连接状态 类型 接口名
    static bool AdapterEnabled()
    {
        try
        {
            string output = WifiManager.RunNetsh("interface show interface");
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                foreach (string line in (output ?? "").Split('\n'))
                    if (line.TrimEnd().EndsWith(adapter.Name, StringComparison.OrdinalIgnoreCase)
                        && Regex.IsMatch(line, "已禁用|Disabled", RegexOptions.IgnoreCase)) return false;
            }
        }
        catch { }
        return true;
    }

    internal static bool SetRadioOn()
    {
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero, data = IntPtr.Zero;
        try
        {
            int negotiated;
            if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0) return false;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return false;
            if (Marshal.ReadInt32(list) < 1) return false;

            Guid guid = (Guid)Marshal.PtrToStructure(new IntPtr(list.ToInt64() + 8), typeof(Guid));

            int size, valueType;
            if (WlanQueryInterface(handle, ref guid, OpcodeRadioState, IntPtr.Zero, out size, out data, out valueType) != 0) return false;

            // WlanQueryInterface 返回的是 WLAN_RADIO_STATE：dwNumberOfPhys(4 字节) + 固定 64 个
            // WLAN_PHY_RADIO_STATE(各 12 字节)。但 WlanSetInterface 的 radio_state 一次只接受
            // 单个 WLAN_PHY_RADIO_STATE，所以逐个 PHY 提交——直接把整块 772 字节传进去会得到
            // ERROR_INVALID_PARAMETER(87)。
            int phys = Marshal.ReadInt32(data);
            if (phys <= 0 || phys > 64) return false;

            bool allApplied = true;
            for (int i = 0; i < phys; i++)
            {
                IntPtr phy = new IntPtr(data.ToInt64() + 4 + i * PhyRadioStateSize);
                Marshal.WriteInt32(phy, 4, 1);   // dot11SoftwareRadioState = ON
                int result = WlanSetInterface(handle, ref guid, OpcodeRadioState, PhyRadioStateSize, phy, IntPtr.Zero);
                if (result != 0)
                {
                    allApplied = false;
                    AppData.Log("设置 phy[" + i + "] 无线电状态返回错误码 " + result);
                }
            }
            return allApplied;
        }
        catch (Exception ex) { AppData.Log("打开无线电时出错：" + ex.Message); return false; }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    static bool LooksLikeFailure(string output)
    {
        return Regex.IsMatch(output ?? "", "error|错误|拒绝|无法|未能|denied|failed|elevation",
            RegexOptions.IgnoreCase);
    }
}

static class BackgroundHost
{
    static System.Threading.Timer timer;
    static readonly object StateGate = new object();
    static bool connected;
    static bool checkQueued;

    internal static void Run(bool startupLaunch)
    {
        using (var mutex = new Mutex(false, AppData.MutexName))
        {
            // 替代进程可能在旧实例刚收到停止信号时启动，等待旧实例退出，避免两个都退出。
            if (!TryAcquireInstance(mutex, 5000)) return;
            try
            {
                bool stopCreated;
                using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, AppData.StopEventName, out stopCreated))
                {
                    stop.Reset();
                    // 仅在后台进程入口检查一次，绝不放进定时重连或网络变化回调。
                    PortalSettings startupSettings;
                    CredentialAccount startupCredential;
                    bool configured = AppData.LoadConnection(out startupSettings, out startupCredential);
                    if (startupLaunch && configured && startupSettings.RepairStartupProxy && startupSettings.Validate() == null
                        && !String.IsNullOrWhiteSpace(startupSettings.PortalUrl))
                        StartupProxyRepair.Run(true, new WindowsStartupProxyEnvironment(stop));
                    if (stop.WaitOne(0)) return;
                    NetworkChange.NetworkAvailabilityChanged += delegate { SetConnected(false); QueueCheck(3000); };
                    NetworkChange.NetworkAddressChanged += delegate { SetConnected(false); QueueCheck(3000); };
                    PortalSettings settings = AppData.LoadSettings();
                    int interval = Math.Max(20, Math.Min(3600, settings.CheckIntervalSeconds));
                    timer = new System.Threading.Timer(delegate { QueueCheck(0); }, null, 500, interval * 1000);
                    AppData.Log("后台服务已启动，未联网时检查间隔 " + interval + " 秒。");
                    stop.WaitOne();
                    timer.Dispose();
                    AppData.Log("后台服务已停止。");
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    internal static bool TryAcquireInstance(Mutex mutex, int timeoutMilliseconds)
    {
        try { return mutex.WaitOne(timeoutMilliseconds); }
        catch (AbandonedMutexException) { return true; }
    }

    static void QueueCheck(int delay)
    {
        lock (StateGate)
        {
            // 网络已确认可用时不再轮询，交给网络变化事件在断网后唤醒。
            if (connected) return;
            if (checkQueued) return;
            checkQueued = true;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (delay > 0) Thread.Sleep(delay);
                PortalSettings settings = AppData.LoadSettings();
                bool online = PortalClient.HasInternet();
                if (online)
                {
                    SetConnected(true);
                    // 网络确实通了，之前对某个无线网络的「不可用」判定作废。

                }
                else
                {
                    SetConnected(false);
                    PortalClient.TryLogin();
                }
            }
            finally
            {
                lock (StateGate) checkQueued = false;
            }
        });
    }

    static void SetConnected(bool value)
    {
        lock (StateGate)
        {
            connected = value;
            if (timer != null)
            {
                int intervalMs = Math.Max(20, Math.Min(3600, AppData.LoadSettings().CheckIntervalSeconds)) * 1000;
                timer.Change(value ? Timeout.Infinite : intervalMs, value ? Timeout.Infinite : intervalMs);
            }
        }
    }
}

internal enum ScheduledTaskPresence { Present, Absent, Unknown }

static class StartupManager
{
    // 强连接模式要处理「无线网卡被禁用」，启停网卡必须管理员权限，所以这种模式下用一个
    // 「以最高权限运行」的开机计划任务来承载后台服务（开机不弹 UAC）；
    // 普通模式仍旧走注册表 Run 键。两者二选一，避免重复拉起。
    internal const string TaskName = "CampusFlow_Community";

    internal static bool IsAdministrator()
    {
        try
        {
            var principal = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent());
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    internal static bool ElevatedTaskInstalled() { return QueryElevatedTask() == ScheduledTaskPresence.Present; }

    internal static ScheduledTaskPresence ClassifyTaskQueryError(Exception error)
    {
        while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
        return (error is COMException || error is FileNotFoundException || error is DirectoryNotFoundException)
            && (error.HResult == unchecked((int)0x80070002) || error.HResult == unchecked((int)0x80070003))
            ? ScheduledTaskPresence.Absent : ScheduledTaskPresence.Unknown;
    }

    static ScheduledTaskPresence QueryElevatedTask()
    {
        object service = null, folder = null, task = null;
        bool queryingTask = false;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            service.GetType().InvokeMember("Connect", BindingFlags.InvokeMethod, null, service, new object[0]);
            folder = service.GetType().InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, service, new object[] { @"\" });
            queryingTask = true;
            task = folder.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, folder, new object[] { TaskName });
            return task != null ? ScheduledTaskPresence.Present : ScheduledTaskPresence.Unknown;
        }
        catch (Exception error) { return queryingTask ? ClassifyTaskQueryError(error) : ScheduledTaskPresence.Unknown; }
        finally
        {
            foreach (object value in new[] { task, folder, service })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    // Read only when saving an enabled strong-connect configuration. A renamed
    // or moved executable must update the existing task through the UAC flow.
    internal static bool ElevatedTaskPathNeedsUpdate()
    {
        object service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            service.GetType().InvokeMember("Connect", BindingFlags.InvokeMethod, null, service, new object[0]);
            folder = service.GetType().InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, service, new object[] { @"\" });
            task = folder.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, folder, new object[] { TaskName });
            string xml = (string)task.GetType().InvokeMember("Xml", BindingFlags.GetProperty, null, task, null);
            return TaskCommandNeedsUpdate(xml, Application.ExecutablePath);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("无法确认最高权限开机任务的程序路径，未更改自启或重启后台。请检查任务后重试。", error);
        }
        finally
        {
            foreach (object value in new[] { task, folder, service })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    internal static bool TaskCommandNeedsUpdate(string taskXml, string currentExecutable)
    {
        try
        {
            if (String.IsNullOrWhiteSpace(taskXml)) throw new InvalidOperationException("开机任务定义为空。");
            var document = new XmlDocument { XmlResolver = null };
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var input = new StringReader(taskXml))
            using (XmlReader reader = XmlReader.Create(input, readerSettings)) document.Load(reader);
            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            XmlNodeList actions = document.SelectNodes("/t:Task/t:Actions/*", namespaces);
            XmlNodeList commands = document.SelectNodes("/t:Task/t:Actions/t:Exec/t:Command", namespaces);
            if (actions.Count != 1 || commands.Count != 1) throw new InvalidOperationException("开机任务必须包含一个程序启动命令。");
            return !String.Equals(NormalizeExecutablePath(commands[0].InnerText), NormalizeExecutablePath(currentExecutable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("无法确认最高权限开机任务的程序路径。", error);
        }
    }

    static string NormalizeExecutablePath(string value)
    {
        string path = Environment.ExpandEnvironmentVariables((value ?? "").Trim());
        if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"') path = path.Substring(1, path.Length - 2);
        string root = Path.GetPathRoot(path);
        if (String.IsNullOrWhiteSpace(root) || root == @"\" || root.EndsWith(":", StringComparison.Ordinal))
            throw new InvalidOperationException("开机任务程序路径必须是完整的 Windows 路径。");
        return Path.GetFullPath(path);
    }

    internal static bool IsEnabled
    {
        get
        {
            if (ElevatedTaskInstalled()) return true;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppData.RunKey))
                return key != null && key.GetValue(AppData.RunName) != null;
        }
    }

    internal static string BuildElevatedTaskXml(string exe, string userId)
    {
        userId = new System.Security.Principal.SecurityIdentifier(userId).Value;
        const string taskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = new XmlDocument { XmlResolver = null };
        document.AppendChild(document.CreateXmlDeclaration("1.0", "UTF-16", null));
        XmlElement task = document.CreateElement("Task", taskNamespace);
        task.SetAttribute("version", "1.2");
        document.AppendChild(task);
        XmlElement registration = AddTaskElement(document, task, "RegistrationInfo", null);
        AddTaskElement(document, registration, "Description", "CampusRelink 校园网自动重连；用户登录后自动运行。");
        XmlElement triggers = AddTaskElement(document, task, "Triggers", null);
        XmlElement logon = AddTaskElement(document, triggers, "LogonTrigger", null);
        AddTaskElement(document, logon, "Enabled", "true");
        AddTaskElement(document, logon, "UserId", userId);
        XmlElement principals = AddTaskElement(document, task, "Principals", null);
        XmlElement principal = AddTaskElement(document, principals, "Principal", null);
        principal.SetAttribute("id", "CampusFlowUser");
        AddTaskElement(document, principal, "UserId", userId);
        AddTaskElement(document, principal, "LogonType", "InteractiveToken");
        AddTaskElement(document, principal, "RunLevel", "HighestAvailable");
        XmlElement settings = AddTaskElement(document, task, "Settings", null);
        AddTaskElement(document, settings, "MultipleInstancesPolicy", "IgnoreNew");
        AddTaskElement(document, settings, "DisallowStartIfOnBatteries", "false");
        AddTaskElement(document, settings, "StopIfGoingOnBatteries", "false");
        AddTaskElement(document, settings, "AllowHardTerminate", "true");
        // 不补跑过去的登录触发器：保存自启配置不会立即启动后台或操作当前网络。
        AddTaskElement(document, settings, "StartWhenAvailable", "false");
        AddTaskElement(document, settings, "RunOnlyIfNetworkAvailable", "false");
        AddTaskElement(document, settings, "Enabled", "true");
        AddTaskElement(document, settings, "ExecutionTimeLimit", "PT0S");
        AddTaskElement(document, settings, "Priority", "7");
        XmlElement actions = AddTaskElement(document, task, "Actions", null);
        actions.SetAttribute("Context", "CampusFlowUser");
        XmlElement action = AddTaskElement(document, actions, "Exec", null);
        AddTaskElement(document, action, "Command", exe);
        AddTaskElement(document, action, "Arguments", "--background --startup");
        return document.OuterXml;
    }

    static XmlElement AddTaskElement(XmlDocument document, XmlElement parent, string name, string value)
    {
        XmlElement element = document.CreateElement(name, parent.NamespaceURI);
        if (value != null) element.InnerText = value;
        parent.AppendChild(element);
        return element;
    }

    static void RegisterElevatedTask(string exe, string userId)
    {
        string xml = BuildElevatedTaskXml(exe, userId);
        Directory.CreateDirectory(AppData.DirectoryPath);
        string definition = Path.Combine(AppData.DirectoryPath, "autostart-task.xml");
        File.WriteAllText(definition, xml, Encoding.Unicode);
        var info = new ProcessStartInfo("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + definition + "\" /F")
        {
            CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (Process process = Process.Start(info))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("注册开机任务失败：" + output.Trim() + " " + error.Trim());
        }
    }

    // 仅保存自启配置：不停止旧后台、不启动新后台、不触发校园网认证或更改代理。
    internal static void ConfigureStartupOnly(string userId)
    {
        userId = new System.Security.Principal.SecurityIdentifier(userId).Value;
        RegisterElevatedTask(Application.ExecutablePath, userId);
        using (RegistryKey key = Registry.Users.OpenSubKey(userId + "\\" + AppData.RunKey, true))
            if (key != null) key.DeleteValue(AppData.RunName, false);
    }

    // 普通启动路径。
    internal static void Enable()
    {
        ScheduledTaskPresence presence = QueryElevatedTask();
        if (presence == ScheduledTaskPresence.Unknown) throw new InvalidOperationException("无法确认自启任务状态，请稍后重试。");
        if (presence == ScheduledTaskPresence.Absent)
            WriteRunKey("\"" + Application.ExecutablePath + "\" --background --startup");
        Stop();
        Process.Start(Application.ExecutablePath, "--background --runtime");
    }

    // 由提权后的 --elevated-setup 调用：安装或卸载最高权限开机任务。
    internal static void ApplyElevatedMode(bool elevated)
    {
        string exe = Application.ExecutablePath;
        if (elevated)
        {
            RegisterElevatedTask(exe, System.Security.Principal.WindowsIdentity.GetCurrent().User.Value);
            WriteRunKey(null);   // 计划任务已负责开机启动，注册表那份撤掉
        }
        else
        {
            DeleteElevatedTask();
            WriteRunKey("\"" + exe + "\" --background --startup");
        }

        Stop();
        Thread.Sleep(600);
        Process.Start(exe, "--background --runtime");
    }

    internal static void Disable()
    {
        WriteRunKey(null);
        DeleteElevatedTask();
        Stop();
    }

    // 弹一次 UAC，让 --elevated-setup 以管理员身份完成安装/卸载。返回是否成功发起。
    internal static bool Elevate()
    {
        try
        {
            string userId = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            var info = new ProcessStartInfo(Application.ExecutablePath, "--elevated-setup " + userId)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            using (Process process = Process.Start(info))
            {
                // 等它做完再返回，界面才能准确报告结果；提权实例本身不弹窗，不会卡住这里。
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("未能完成自启权限配置，请检查运行日志。");
            }
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("系统授权被取消或失败：" + ex.Message + "\r\n\r\n将继续按普通权限运行。",
                "CampusRelink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    internal static void Restart()
    {
        if (!IsEnabled) return;
        Stop();
        Thread.Sleep(500);
        Process.Start(Application.ExecutablePath, "--background --runtime");
    }

    static void WriteRunKey(string value)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppData.RunKey))
            {
                if (value == null) key.DeleteValue(AppData.RunName, false);
                else key.SetValue(AppData.RunName, value);
            }
        }
        catch { }
    }

    // 只停后台服务，保留自启配置——托盘「退出」用的就是这个。
    internal static void StopBackground() { Stop(); }

    static void Stop()
    {
        try { EventWaitHandle.OpenExisting(AppData.StopEventName).Set(); }
        catch { }
    }

    static void DeleteElevatedTask()
    {
        ScheduledTaskPresence presence = QueryElevatedTask();
        if (presence == ScheduledTaskPresence.Absent) return;
        if (presence == ScheduledTaskPresence.Unknown) throw new InvalidOperationException("无法确认自启任务状态，保留当前后台。");
        var info = new ProcessStartInfo("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F")
        { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        using (Process process = Process.Start(info))
        {
            if (!process.WaitForExit(6000))
            {
                try { process.Kill(); } catch { }
                throw new InvalidOperationException("移除自启任务超时，原后台保持运行。");
            }
            if (process.ExitCode != 0 || QueryElevatedTask() != ScheduledTaskPresence.Absent)
                throw new InvalidOperationException("未能移除自启任务，请授权后重试。");
        }
    }
}

static class NativeWindow
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    internal static void ApplyBackdrop(IntPtr handle)
    {
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            int acrylic = 3;
            DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int));
            int corners = 2;
            DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        }
        catch { }
    }
}

sealed class RoundPanel : Panel
{
    public Color FillColor { get; set; }
    public Color BorderColor { get; set; }

    public RoundPanel()
    {
        FillColor = Color.FromArgb(196, 31, 40, 52);
        BorderColor = Color.FromArgb(70, 255, 255, 255);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = Rounded(ClientRectangle, 8))
        using (SolidBrush brush = new SolidBrush(FillColor))
        using (Pen pen = new Pen(BorderColor))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }
        base.OnPaint(e);
    }

    static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, d, d, 180, 90);
        path.AddArc(rectangle.Right - d - 1, rectangle.Top, d, d, 270, 90);
        path.AddArc(rectangle.Right - d - 1, rectangle.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

sealed class GlassButton : Button
{
    bool hovered;
    bool pressed;

    public GlassButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.FromArgb(37, 139, 244);
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        Height = 36;
        DoubleBuffered = true;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color surface = Parent == null ? Color.FromArgb(22, 31, 43) : Parent.BackColor;
        RoundPanel parentCard = Parent as RoundPanel;
        if (parentCard != null) surface = parentCard.FillColor;
        if (surface.A < 255) surface = Color.FromArgb(surface.R, surface.G, surface.B);
        e.Graphics.Clear(surface);
        Rectangle bounds = new Rectangle(1, 1, Math.Max(2, Width - 3), Math.Max(2, Height - 3));
        int radius = Math.Min(10, Math.Min(bounds.Width, bounds.Height) / 2);
        Color baseColor = Enabled ? BackColor : Color.FromArgb(80, 88, 101);
        Color top = Blend(baseColor, Color.White, hovered ? 34 : 18);
        Color bottom = pressed ? Blend(baseColor, Color.Black, 18) : Blend(baseColor, Color.Black, 5);

        using (GraphicsPath path = Rounded(bounds, radius))
        using (var fill = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical))
        using (var border = new Pen(Color.FromArgb(hovered ? 130 : 72, 255, 255, 255), hovered ? 1.1F : 0.8F))
        {
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }

        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.FromArgb(175, 185, 196),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    static Color Blend(Color from, Color to, int amount)
    {
        int inverse = 255 - amount;
        return Color.FromArgb(from.A,
            (from.R * inverse + to.R * amount) / 255,
            (from.G * inverse + to.G * amount) / 255,
            (from.B * inverse + to.B * amount) / 255);
    }

    static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, d, d, 180, 90);
        path.AddArc(rectangle.Right - d, rectangle.Top, d, d, 270, 90);
        path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

sealed class MainForm : Form
{
    readonly Color TextMain = Color.FromArgb(243, 247, 252);
    readonly Color TextMuted = Color.FromArgb(169, 182, 198);
    readonly TextBox username = Input();
    readonly SavedPasswordTextBox password = new SavedPasswordTextBox();
    readonly NumericUpDown interval = new NumericUpDown();
    readonly ComboBox wifiProfile = Choice();
    readonly TextBox loginUrl = Input();
    readonly CheckBox autoConnectWifi = new CheckBox();
    readonly Label wifiNotice = new Label();
    readonly GlassButton searchWifi = new GlassButton();
    readonly IWifiProfileSource wifiSource;
    WifiProfileSnapshot wifiSnapshot;
    readonly CheckBox repairStartupProxy = new CheckBox();
    readonly CheckBox forceConnect = new CheckBox();
    readonly Label status = new Label();
    readonly TabControl tabs = new TabControl();
    readonly List<GlassButton> navigationButtons = new List<GlassButton>();
    readonly RichTextBox log = new RichTextBox();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
    ToolStripMenuItem trayStatus;
    GlassButton themeButton;
    Icon applicationIcon;
    Image aboutIcon;
    bool nightMode = true;
    // 点关闭按钮只收进托盘；只有托盘菜单里的「退出」会把这两个标志立起来。
    bool exiting;
    bool trayHintShown;

    internal MainForm(bool showTray = true, IWifiProfileSource profileSource = null)
    {
        AutoScaleMode = AutoScaleMode.None;
        wifiSource = profileSource ?? new WindowsWifiProfileSource();
        TrySetIcon();
        Text = "CampusRelink";
        ClientSize = new Size(900, 650);
        MinimumSize = new Size(820, 610);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 26, 35);
        Font = new Font("Microsoft YaHei UI", 9F);
        DoubleBuffered = true;
        Padding = new Padding(1);

        BuildHeader();
        BuildTabs();
        if (showTray) BuildTray();
        LoadValues();
        UpdateStatus();
    }

    void TrySetIcon()
    {
        try
        {
            applicationIcon = BrandAssets.LoadIcon(32);
            Icon = applicationIcon;
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeWindow.ApplyBackdrop(Handle);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Color top = nightMode ? Color.FromArgb(19, 29, 42) : Color.FromArgb(239, 244, 249);
        Color bottom = nightMode ? Color.FromArgb(15, 22, 31) : Color.FromArgb(218, 228, 238);
        using (var gradient = new LinearGradientBrush(ClientRectangle, top, bottom, 32F))
            e.Graphics.FillRectangle(gradient, ClientRectangle);
        using (var pen = new Pen(nightMode ? Color.FromArgb(24, 114, 199, 229) : Color.FromArgb(24, 64, 133, 180), 80F))
        using (var greenPen = new Pen(nightMode ? Color.FromArgb(18, 86, 214, 151) : Color.FromArgb(22, 49, 164, 132), 64F))
        {
            e.Graphics.DrawLine(pen, -100, Height - 50, Width / 2, -80);
            e.Graphics.DrawLine(greenPen, Width / 3, Height + 60, Width + 80, 80);
        }
    }

    void BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Color.Transparent };
        header.MouseDown += DragWindow;
        var brand = new Label { Text = "CampusRelink", ForeColor = TextMain, Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold), AutoSize = true, Location = new Point(28, 18) };
        brand.MouseDown += DragWindow;
        var subtitle = new Label { Text = "校园网自动重连", ForeColor = TextMuted, AutoSize = true, Location = new Point(31, 54) };
        subtitle.MouseDown += DragWindow;
        header.Controls.Add(brand);
        header.Controls.Add(subtitle);

        status.AutoSize = false;
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.SetBounds(655, 25, 145, 32);
        status.ForeColor = TextMain;
        status.BackColor = Color.FromArgb(55, 255, 255, 255);
        header.Controls.Add(status);

        var minimize = HeaderButton("−", 812);
        minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
        var close = HeaderButton("×", 854);
        close.Click += delegate { Close(); };
        header.Controls.Add(minimize);
        header.Controls.Add(close);
        themeButton = new GlassButton { Text = "☀ 日间模式", Location = new Point(505, 23), Size = new Size(135, 36), BackColor = Color.FromArgb(48, 61, 78), ForeColor = TextMain };
        themeButton.Click += delegate { ToggleTheme(); };
        header.Controls.Add(themeButton);
        Controls.Add(header);
    }

    Button HeaderButton(string text, int x)
    {
        var button = new Button { Text = text, Location = new Point(x, 18), Size = new Size(34, 34), ForeColor = TextMain, BackColor = Color.Transparent, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 13F) };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 255, 255, 255);
        return button;
    }

    // 托盘图标：关窗口不等于退程序，真正的出口在托盘右键菜单里。
    void BuildTray()
    {
        try
        {
            tray.Icon = applicationIcon ?? SystemIcons.Application;
        }
        catch { tray.Icon = SystemIcons.Application; }

        tray.Text = "CampusRelink";
        tray.Visible = true;
        tray.DoubleClick += delegate { RestoreWindow(); };

        var open = new ToolStripMenuItem("打开主界面");
        open.Click += delegate { RestoreWindow(); };

        trayStatus = new ToolStripMenuItem("后台服务：未知");
        trayStatus.Enabled = false;

        var quit = new ToolStripMenuItem("退出（停止后台服务）");
        quit.Click += delegate { ExitApplication(); };

        trayMenu.Items.Add(open);
        trayMenu.Items.Add(trayStatus);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(quit);
        tray.ContextMenuStrip = trayMenu;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            trayMenu.Dispose();
            if (aboutIcon != null) { aboutIcon.Dispose(); aboutIcon = null; }
        }
        base.Dispose(disposing);
        if (disposing && applicationIcon != null) { applicationIcon.Dispose(); applicationIcon = null; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 点关闭按钮（或 Alt+F4）只收进托盘；只有托盘菜单里的「退出」才真的结束程序。
        AppData.Log("收到窗口关闭请求：reason=" + e.CloseReason + "，exiting=" + exiting);
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                try
                {
                    tray.ShowBalloonTip(3000, "CampusRelink",
                        "已收到托盘，程序仍在后台保持连接。双击图标可重新打开，右键可退出。",
                        ToolTipIcon.Info);
                }
                catch { }
            }
            return;
        }
        base.OnFormClosing(e);
    }

    void RestoreWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void ExitApplication()
    {
        exiting = true;
        StartupManager.StopBackground();   // 停后台服务，但不动自启配置
        tray.Visible = false;
        Close();
        Application.Exit();
    }

    void DragWindow(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        NativeWindow.ReleaseCapture();
        NativeWindow.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }

    void BuildTabs()
    {
        var workspace = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 25, 35) };
        var navigation = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(24, 10, 24, 10), BackColor = Color.FromArgb(22, 31, 43) };

        tabs.Dock = DockStyle.Fill;
        tabs.ItemSize = new Size(0, 1);
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.Appearance = TabAppearance.FlatButtons;
        tabs.Controls.Add(BuildBasicPage());
        tabs.Controls.Add(BuildLogPage());
        tabs.Controls.Add(BuildAboutPage());

        string[] titles = { "账户与连接", "运行日志", "关于" };
        for (int i = 0; i < titles.Length; i++)
        {
            int index = i;
            var button = new GlassButton
            {
                Text = titles[i],
                Location = new Point(24 + i * 142, 10),
                Size = new Size(130, 38),
                BackColor = i == 0 ? Color.FromArgb(42, 139, 236) : Color.FromArgb(48, 61, 78),
                ForeColor = TextMain
            };
            button.Click += delegate { SelectPage(index); };
            navigationButtons.Add(button);
            navigation.Controls.Add(button);
        }

        workspace.Controls.Add(tabs);
        workspace.Controls.Add(navigation);
        Controls.Add(workspace);
        workspace.BringToFront();
    }

    void SelectPage(int index)
    {
        tabs.SelectedIndex = index;
        for (int i = 0; i < navigationButtons.Count; i++)
        {
            navigationButtons[i].BackColor = i == index ? Color.FromArgb(42, 139, 236)
                : (nightMode ? Color.FromArgb(48, 61, 78) : Color.FromArgb(214, 226, 239));
            navigationButtons[i].ForeColor = i == index || nightMode ? Color.White : Color.FromArgb(31, 41, 55);
        }
    }

    TabPage BuildBasicPage()
    {
        var page = Page("账户与连接");
        var card = Card(28, 25, 825, 460);
        page.Controls.Add(card);
        AddField(card, "校园网账号", username, 28, 18, 360);
        AddField(card, "密码（已保存显示 15 个 *，输入可替换）", password, 428, 18, 360);
        AddField(card, "认证接口链接", loginUrl, 28, 96, 760);
        autoConnectWifi.Text = "自动连接 Wi-Fi（断线后重新连接所选网络）";
        autoConnectWifi.AutoSize = true;
        autoConnectWifi.Location = new Point(30, 173);
        autoConnectWifi.CheckedChanged += delegate { UpdateWifiMode(); };
        card.Controls.Add(autoConnectWifi);
        wifiProfile.DropDownStyle = ComboBoxStyle.DropDownList;
        wifiProfile.SelectedIndexChanged += delegate { UpdateWifiMode(); };
        AddField(card, "校园网 Wi-Fi（搜索当前电脑附近的网络）", wifiProfile, 28, 203, 380);
        searchWifi.Text = "搜索 Wi-Fi";
        searchWifi.SetBounds(420, 228, 110, 30);
        searchWifi.Click += delegate { SearchWifi(); };
        card.Controls.Add(searchWifi);
        interval.Minimum = 20;
        interval.Maximum = 3600;
        AddField(card, "断网检查间隔（秒）", interval, 548, 203, 240);
        wifiNotice.SetBounds(28, 269, 760, 34);
        wifiNotice.ForeColor = TextMuted;
        card.Controls.Add(wifiNotice);
        forceConnect.Text = "强连接：无线开关关闭时尝试打开";
        forceConnect.AutoSize = true;
        forceConnect.Location = new Point(30, 309);
        card.Controls.Add(forceConnect);
        repairStartupProxy.Text = "开机时检查遗留的无效本机代理";
        repairStartupProxy.AutoSize = true;
        repairStartupProxy.Location = new Point(428, 309);
        card.Controls.Add(repairStartupProxy);
        var save = new GlassButton { Text = "保存并启用", Location = new Point(28, 349), Width = 145 };
        save.Click += delegate { SaveAndEnable(); };
        var test = new GlassButton { Text = "测试登录", Location = new Point(183, 349), Width = 125, BackColor = Color.FromArgb(50, 185, 143) };
        test.Click += delegate { TestLogin(test); };
        var disable = new GlassButton { Text = "停止后台", Location = new Point(318, 349), Width = 125, BackColor = Color.FromArgb(73, 84, 102) };
        disable.Click += delegate {
            try { StartupManager.Disable(); UpdateStatus(); MessageBox.Show("后台运行和开机启动已停止。", "CampusRelink"); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "CampusRelink"); }
        };
        card.Controls.Add(save); card.Controls.Add(test); card.Controls.Add(disable);
        card.Controls.Add(new Label {
            Text = "默认适配南京中医药大学，认证接口已预填，可按需修改。\r\n"
                + "关闭自动连接时仅负责认证，请自行接入校园 Wi-Fi 或校园网网线。",
            ForeColor = TextMuted, AutoSize = true, Location = new Point(28, 404)
        });
        return page;
    }

    TabPage BuildLogPage()
    {
        var page = Page("运行日志");
        var card = Card(28, 25, 825, 455);
        page.Controls.Add(card);
        log.SetBounds(24, 24, 777, 345);
        log.ReadOnly = true;
        log.BorderStyle = BorderStyle.None;
        log.BackColor = Color.FromArgb(23, 31, 42);
        log.ForeColor = Color.FromArgb(199, 211, 226);
        log.Font = new Font("Consolas", 9F);
        card.Controls.Add(log);
        var refresh = new GlassButton { Text = "刷新", Location = new Point(24, 392), Width = 100 };
        refresh.Click += delegate { LoadLog(); };
        var folder = new GlassButton { Text = "打开日志目录", Location = new Point(139, 392), Width = 140, BackColor = Color.FromArgb(73, 84, 102) };
        folder.Click += delegate { Directory.CreateDirectory(AppData.DirectoryPath); Process.Start("explorer.exe", AppData.DirectoryPath); };
        card.Controls.Add(refresh);
        card.Controls.Add(folder);
        page.Enter += delegate { LoadLog(); };
        return page;
    }

    TabPage BuildAboutPage()
    {
        var page = Page("关于");
        var card = Card(52, 42, 775, 390);
        page.Controls.Add(card);

        try { using (Icon icon = BrandAssets.LoadIcon(64)) aboutIcon = icon.ToBitmap(); }
        catch { aboutIcon = null; }
        var mark = new PictureBox
        {
            Image = aboutIcon,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Location = new Point(38, 42),
            Size = new Size(62, 62)
        };
        var title = new Label { Text = "CampusRelink", ForeColor = TextMain, Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold), AutoSize = true, Location = new Point(122, 43) };
        var version = new Label { Text = "Version 1.0.3  ·  Windows 10 / 11", ForeColor = TextMuted, AutoSize = true, Location = new Point(125, 82) };
        var description = new Label
        {
            Text = "本软件默认适配南京中医药大学的校园网认证。\r\nCampusRelink 支持自动认证、断线重连、托盘运行与开机启动。\r\n认证接口可修改；账号密码由当前 Windows 用户的 DPAPI 加密保护。",
            ForeColor = Color.FromArgb(218, 228, 239),
            Font = new Font("Microsoft YaHei UI", 10.5F),
            Location = new Point(40, 145),
            Size = new Size(690, 92),
            AutoEllipsis = false
        };
        var divider = new Panel { Location = new Point(40, 258), Size = new Size(690, 1), BackColor = Color.FromArgb(65, 255, 255, 255) };
        var creditLabel = new Label { Text = "发布信息", ForeColor = TextMuted, AutoSize = true, Location = new Point(40, 284) };
        var credit = new Label
        {
            Text = "CampusRelink contributors · MIT License",
            ForeColor = TextMain,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(40, 313)
        };
        card.Controls.Add(mark);
        card.Controls.Add(title);
        card.Controls.Add(version);
        card.Controls.Add(description);
        card.Controls.Add(divider);
        card.Controls.Add(creditLabel);
        card.Controls.Add(credit);
        return page;
    }

    static TabPage Page(string title)
    {
        return new TabPage(title) { BackColor = Color.FromArgb(18, 25, 35), Padding = new Padding(0) };
    }

    RoundPanel Card(int x, int y, int width, int height)
    {
        return new RoundPanel { Location = new Point(x, y), Size = new Size(width, height), Anchor = AnchorStyles.Top | AnchorStyles.Left };
    }

    void AddField(Control parent, string labelText, Control field, int x, int y, int width)
    {
        var label = new Label { Text = labelText, ForeColor = TextMuted, AutoSize = true, Location = new Point(x, y) };
        field.SetBounds(x, y + 25, width, field.Height > 30 ? field.Height : 30);
        parent.Controls.Add(label);
        parent.Controls.Add(field);
    }

    static TextBox Input(bool multiline = false)
    {
        return new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(31, 41, 54),
            ForeColor = Color.FromArgb(243, 247, 252),
            Multiline = multiline,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
    }

    // 下拉框同时允许直接输入：列表来自本机已保存的无线配置，但也可以填一个还没保存过的 SSID。
    static ComboBox Choice()
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(31, 41, 54),
            ForeColor = Color.FromArgb(243, 247, 252),
            Font = new Font("Microsoft YaHei UI", 9F)
        };
    }

    void LoadValues()
    {
        PortalSettings settings = AppData.LoadSettings();
        nightMode = settings.NightMode;
        interval.Value = Math.Max(interval.Minimum, Math.Min(interval.Maximum, settings.CheckIntervalSeconds));
        loginUrl.Text = settings.LoginUrl;
        repairStartupProxy.Checked = settings.RepairStartupProxy;
        CredentialAccount savedCredential = CredentialStore.Load();
        username.Text = savedCredential.Username;
        password.ShowSavedPassword(savedCredential.IsComplete);
        RefreshWifiChoices(settings.WifiProfile);
        autoConnectWifi.Checked = File.Exists(AppData.SettingsPath) ? settings.AutoConnectWifi : !String.IsNullOrEmpty(wifiProfile.Text);
        forceConnect.Checked = autoConnectWifi.Checked && settings.ForceConnect;
        UpdateWifiMode();
        ApplyTheme();
    }

    void RefreshWifiChoices(string preferred)
    {
        WifiProfileSnapshot snapshot;
        try { snapshot = wifiSource.Read(); }
        catch { snapshot = new WifiProfileSnapshot { Notice = "无法读取这台电脑的 Wi-Fi，请检查 Windows 无线服务。" }; }
        ApplyWifiChoices(snapshot, preferred);
    }

    void SearchWifi()
    {
        if (!searchWifi.Enabled) return;
        searchWifi.Enabled = false;
        searchWifi.Text = "搜索中…";
        wifiNotice.Text = "正在搜索这台电脑附近的 Wi-Fi，请稍候…";
        ThreadPool.QueueUserWorkItem(delegate {
            WifiProfileSnapshot found;
            try
            {
                var search = wifiSource as IWifiSearchSource;
                found = search != null ? search.Search() : wifiSource.Read();
            }
            catch { found = new WifiProfileSnapshot { Notice = "搜索失败，请检查 Windows 无线开关与 Wi-Fi 权限。" }; }
            if (IsDisposed || Disposing) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate {
                    if (IsDisposed || Disposing) return;
                    var latest = wifiProfile.SelectedItem as WifiNetworkChoice;
                    ApplyWifiChoices(found, latest == null ? null : latest.ProfileName, latest == null ? null : latest.Ssid);
                    searchWifi.Text = "搜索 Wi-Fi";
                    searchWifi.Enabled = true;
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    void ApplyWifiChoices(WifiProfileSnapshot snapshot, string preferred, string preferredSsid = null)
    {
        wifiSnapshot = snapshot ?? new WifiProfileSnapshot();
        wifiSnapshot.Profiles = WifiProfileChoices.Normalize(wifiSnapshot.Profiles);
        var choices = WifiProfileChoices.NetworkChoices(wifiSnapshot);
        string selectedProfile = WifiProfileChoices.ChooseProfile(preferred, wifiSnapshot);
        WifiNetworkChoice chosen = null;
        if (String.IsNullOrEmpty(preferred) && !String.IsNullOrEmpty(preferredSsid))
            chosen = choices.Find(delegate(WifiNetworkChoice item) { return String.Equals(item.Ssid, preferredSsid, StringComparison.Ordinal); });
        // A real saved target may currently be out of range. Keep its identity,
        // without pretending it was discovered nearby or adding another PC's data.
        if (!String.IsNullOrEmpty(selectedProfile) && !choices.Exists(delegate(WifiNetworkChoice item) {
            return String.Equals(item.ProfileName, selectedProfile, StringComparison.OrdinalIgnoreCase);
        })) choices.Add(new WifiNetworkChoice { Ssid = selectedProfile, ProfileName = selectedProfile,
            IsAvailable = String.Equals(selectedProfile, wifiSnapshot.ConnectedProfile, StringComparison.OrdinalIgnoreCase) });
        wifiProfile.BeginUpdate();
        try
        {
            wifiProfile.Items.Clear();
            foreach (WifiNetworkChoice choice in choices) wifiProfile.Items.Add(choice);
            wifiProfile.SelectedIndex = -1;
            if (chosen != null) wifiProfile.SelectedItem = chosen;
            foreach (WifiNetworkChoice choice in choices)
                if (chosen == null && !String.IsNullOrEmpty(selectedProfile) && String.Equals(choice.ProfileName, selectedProfile, StringComparison.OrdinalIgnoreCase))
                { wifiProfile.SelectedItem = choice; break; }
        }
        finally { wifiProfile.EndUpdate(); }
        UpdateWifiMode();
    }

    void UpdateWifiMode()
    {
        bool enabled = autoConnectWifi.Checked;
        wifiProfile.Enabled = enabled;
        forceConnect.Enabled = enabled;
        if (!enabled) forceConnect.Checked = false;
        string discovery = wifiSnapshot == null ? "点击搜索 Wi-Fi，从这台电脑获取附近网络。"
            : (wifiSnapshot.QuerySucceeded
                ? "当前附近 Wi-Fi：" + WifiProfileChoices.NetworkChoices(wifiSnapshot).Count + " 个；可再次搜索以更新结果。"
                : "Wi-Fi 读取不完整，请检查 Windows 无线开关、位置权限及 WLAN 服务，再次搜索。");
        wifiNotice.AccessibleDescription = wifiSnapshot == null ? discovery : wifiSnapshot.Notice;
        var selected = wifiProfile.SelectedItem as WifiNetworkChoice;
        string mode = !enabled ? "仅认证模式：请先接入校园 Wi-Fi 或校园网网线。"
            : (selected != null && !selected.IsSaved
                ? "此网络尚未保存：请先在 Windows 中连接一次，再点击搜索 Wi-Fi。"
                : "选择校园网后可自动重连；搜索结果只来自当前这台电脑。");
        wifiNotice.Text = discovery + "\r\n" + mode;
    }

    string WifiSelectionError()
    {
        if (!autoConnectWifi.Checked) return null;
        var selected = wifiProfile.SelectedItem as WifiNetworkChoice;
        if (selected == null) return "请先点击「搜索 Wi-Fi」，再选择校园网。";
        if (!selected.IsSaved || wifiSnapshot == null || WifiProfileChoices.Find(wifiSnapshot.Profiles, selected.ProfileName) == null)
            return "这个 Wi-Fi 尚未在这台电脑保存连接配置。请先在 Windows 中连接一次，再重新搜索。";
        return null;
    }

    PortalSettings ReadSettings()
    {
        var settings = PortalSettings.Default();
        var selected = wifiProfile.SelectedItem as WifiNetworkChoice;
        settings.CheckIntervalSeconds = (int)interval.Value;
        settings.NightMode = nightMode;
        settings.AutoConnectWifi = autoConnectWifi.Checked;
        settings.WifiProfile = settings.AutoConnectWifi && selected != null ? (selected.ProfileName ?? "") : "";
        settings.ForceConnect = settings.AutoConnectWifi && forceConnect.Checked;
        settings.LoginUrl = loginUrl.Text.Trim();
        settings.RepairStartupProxy = repairStartupProxy.Checked;
        BuiltInAuthentication.Apply(settings);
        return settings;
    }

    bool SaveValues()
    {
        try
        {
            PortalSettings settings = ReadSettings();
            string error = WifiSelectionError() ?? settings.Validate();
            if (error != null) { MessageBox.Show(error, "CampusRelink"); return false; }
            CredentialAccount credential = CredentialEditor.Build(CredentialStore.Load(), username.Text, password.EnteredPassword);
            AppData.SaveConnection(settings, credential);
            password.ShowSavedPassword(true);
            return true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "CampusRelink"); return false; }
    }

    void SaveAndEnable()
    {
        try { SaveAndEnableCore(); }
        catch (Exception ex) { AppData.Log("启用后台未完成。"); MessageBox.Show(ex.Message, "CampusRelink"); }
    }

    void SaveAndEnableCore()
    {
        if (!SaveValues()) return;
        PortalSettings settings = AppData.LoadSettings();

        bool wantElevated = settings.ForceConnect;
        bool elevatedTaskInstalled = StartupManager.ElevatedTaskInstalled();
        bool updateTaskPath = wantElevated && elevatedTaskInstalled && StartupManager.ElevatedTaskPathNeedsUpdate();
        if (wantElevated != elevatedTaskInstalled || updateTaskPath)
        {
            // 强连接模式可能要去启用一张被禁用的网卡，那一步只有管理员做得到。
            // 用「以最高权限运行」的开机计划任务承载后台服务：只需在这里授权一次，开机不再弹窗。
            if (StartupManager.IsAdministrator())
            {
                StartupManager.ApplyElevatedMode(wantElevated);
                UpdateStatus();
                MessageBox.Show(updateTaskPath
                    ? "已更新最高权限开机任务到 CampusRelink 当前程序位置，后台服务已按新配置重启。"
                    : wantElevated
                    ? "已注册最高权限的开机任务，强连接模式现在可以重新启用被禁用的无线网卡。"
                    : "已移除最高权限的开机任务，后台服务改回普通权限启动。", "CampusRelink");
                return;
            }

            string message = updateTaskPath
                ? "软件已更名为 CampusRelink 或程序位置已改变，需要管理员权限更新已有开机任务的程序路径。\r\n\r\n"
                  + "点「是」会弹出系统授权窗口，授权后开机将运行当前的 CampusRelink。\r\n\r\n"
                  + "点「否」则本次按普通权限运行，原开机任务仍指向旧程序。"
                : wantElevated
                ? "强连接模式需要管理员权限。\r\n\r\n"
                  + "无线网卡被「禁用」时，只有管理员才能把它重新启用。点「是」会弹出一次系统授权窗口，"
                  + "授权后程序会注册一个以最高权限运行的开机任务，以后开机不再弹窗。\r\n\r\n"
                  + "点「否」则继续按普通权限运行：强连接模式仍能处理「无线开关被关」和「WLAN 服务未启动」，"
                  + "但处理不了「网卡被禁用」。"
                : "关闭强连接模式需要用管理员权限移除那条最高权限的开机任务。\r\n\r\n点「否」则保持现状不变。";

            if (MessageBox.Show(message, "CampusRelink", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                if (StartupManager.Elevate())
                {
                    UpdateStatus();
                    MessageBox.Show(updateTaskPath
                            ? "已更新最高权限开机任务到 CampusRelink 当前程序位置，后台服务已按新配置重启。"
                            : StartupManager.ElevatedTaskInstalled()
                            ? "已注册最高权限的开机任务，强连接模式现在可以重新启用被禁用的无线网卡。\r\n\r\n后台服务已按新配置重启。"
                            : "已改回普通权限启动。",
                        "CampusRelink");
                    return;   // 已由提权实例完成
                }
            }

            StartupManager.Enable();
            UpdateStatus();
            MessageBox.Show(updateTaskPath
                ? "已保存并按普通权限运行。开机任务仍指向旧程序；请再次保存并授权更新。"
                : wantElevated ? "已保存并启用（普通权限）。" : "已保存，最高权限任务保持不变。", "CampusRelink");
            return;
        }

        StartupManager.Enable();
        UpdateStatus();
        MessageBox.Show("已保存并启用。连接校园网后会在后台自动认证。", "CampusRelink");
    }

    void TestLogin(Control button)
    {
        if (!SaveValues()) return;
        button.Enabled = false;
        status.Text = "正在测试…";
        ThreadPool.QueueUserWorkItem(delegate
        {
            string result = PortalClient.TryLogin();
            string who = PortalClient.GetStatus();
            BeginInvoke((MethodInvoker)delegate
            {
                button.Enabled = true;
                UpdateStatus();
                MessageBox.Show(result + "\r\n\r\n门户账号状态：" + who, "CampusRelink");
            });
        });
    }

    void ToggleTheme()
    {
        nightMode = !nightMode;
        ApplyTheme();
        try { AppData.SaveTheme(nightMode); } catch { }
    }

    void ApplyTheme()
    {
        Color pageColor = nightMode ? Color.FromArgb(18, 25, 35) : Color.FromArgb(244, 247, 251);
        Color panelColor = nightMode ? Color.FromArgb(22, 31, 43) : Color.FromArgb(232, 239, 246);
        Color inputColor = nightMode ? Color.FromArgb(31, 41, 54) : Color.FromArgb(255, 255, 255);
        Color mainText = nightMode ? Color.FromArgb(243, 247, 252) : Color.FromArgb(31, 41, 55);
        Color mutedText = nightMode ? Color.FromArgb(169, 182, 198) : Color.FromArgb(91, 105, 122);

        BackColor = pageColor;
        ApplyThemeToControls(Controls, pageColor, panelColor, inputColor, mainText, mutedText);
        SelectPage(tabs.SelectedIndex);
        if (themeButton != null)
        {
            themeButton.Text = nightMode ? "☀ 日间模式" : "☾ 夜间模式";
            themeButton.BackColor = nightMode ? Color.FromArgb(48, 61, 78) : Color.FromArgb(214, 226, 239);
            themeButton.ForeColor = mainText;
        }
        Invalidate(true);
    }

    void ApplyThemeToControls(Control.ControlCollection controls, Color pageColor, Color panelColor, Color inputColor, Color mainText, Color mutedText)
    {
        foreach (Control control in controls)
        {
            RoundPanel roundPanel = control as RoundPanel;
            GlassButton glassButton = control as GlassButton;
            if (roundPanel != null)
            {
                roundPanel.FillColor = nightMode ? Color.FromArgb(196, 31, 40, 52) : Color.FromArgb(205, 255, 255, 255);
                roundPanel.BorderColor = nightMode ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(120, 116, 137, 157);
            }
            else if (glassButton != null)
            {
                glassButton.ForeColor = Color.White;
            }
            else if (control is Button)
            {
                control.ForeColor = mainText;
                control.BackColor = Color.Transparent;
            }
            else if (control is TextBox || control is RichTextBox || control is ComboBox || control is NumericUpDown)
            {
                control.BackColor = inputColor;
                control.ForeColor = mainText;
            }
            else if (control is Label)
            {
                control.ForeColor = mainText;
            }
            else if (control is CheckBox)
            {
                control.ForeColor = mainText;
                control.BackColor = Color.Transparent;
            }
            else if (control is TabPage)
            {
                control.BackColor = pageColor;
            }
            else if (control is Panel)
            {
                control.BackColor = panelColor;
            }
            if (control == status)
            {
                status.BackColor = nightMode ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(180, 255, 255, 255);
                status.ForeColor = nightMode ? Color.FromArgb(243, 247, 252) : Color.FromArgb(31, 41, 55);
            }
            ApplyThemeToControls(control.Controls, pageColor, panelColor, inputColor, mainText, mutedText);
        }
    }

    void UpdateStatus()
    {
        bool enabled = StartupManager.IsEnabled;
        status.Text = enabled ? "● 后台已启用" : "○ 后台未启用";
        status.ForeColor = enabled ? Color.FromArgb(134, 239, 172) : TextMuted;
        // 托盘菜单与悬浮提示同步反映状态，收进托盘后也能一眼看出还在不在工作。
        if (trayStatus != null) trayStatus.Text = "后台服务：" + (enabled ? "已启用" : "未启用");
        try { if (tray != null && tray.Visible) tray.Text = "CampusRelink · " + (enabled ? "后台已启用" : "后台未启用"); }
        catch { }
    }

    void LoadLog()
    {
        try { log.Text = File.Exists(AppData.LogPath) ? File.ReadAllText(AppData.LogPath, Encoding.UTF8) : "暂无日志"; }
        catch (Exception ex) { log.Text = "读取日志失败：" + ex.Message; }
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }
}

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

    [STAThread]
    static void Main(string[] args)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        if (args.Length > 0 && args[0] == "--configure-startup-only")
        {
            RunStartupConfigurationOnly(args);
            return;
        }
        if (args.Length > 0 && args[0] == "--background")
        {
            // 兼容旧计划任务的单独 --background；开机窗口与一次性标记仍然必须通过。
            BackgroundHost.Run(StartupProxyRepair.IsStartupLaunch(args));
            return;
        }
        if (args.Length > 0 && args[0] == "--elevated-setup")
        {
            RunElevatedSetup(args);
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // 兜住异常，免得界面线程一出错整个进程就没了——托盘常驻模式下这尤其难排查。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            AppData.Log("界面线程异常：" + e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
        {
            AppData.Log("未处理异常：" + e.ExceptionObject);
        };
        Application.Run(new MainForm());
    }

    static void RunStartupConfigurationOnly(string[] args)
    {
        try
        {
            string userId = args.Length > 1 ? args[1] : System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            userId = new System.Security.Principal.SecurityIdentifier(userId).Value;
            if (!StartupManager.IsAdministrator())
            {
                var info = new ProcessStartInfo(Application.ExecutablePath, "--configure-startup-only " + userId)
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit();
                    Environment.ExitCode = process.ExitCode;
                }
                return;
            }
            StartupManager.ConfigureStartupOnly(userId);
            AppData.Log("已保存开机自启任务（仅配置）：" + StartupManager.TaskName + "；没有启动或停止后台。");
        }
        catch (Exception ex)
        {
            AppData.Log("保存开机自启配置失败：" + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    // 这个实例由 UAC 提权启动，装/卸完任务就立刻退出——不弹任何窗，
    // 否则调用方的 WaitForExit 会一直挂在这里等人点确定。
    static void RunElevatedSetup(string[] args)
    {
        try
        {
            string currentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            if (args.Length < 2 || !String.Equals(args[1], currentSid, StringComparison.Ordinal))
                throw new InvalidOperationException("请使用相同 Windows 账户授权；也可选择普通权限运行。");
            PortalSettings settings = AppData.LoadSettings();
            if (settings.Validate() != null) throw new InvalidOperationException("当前用户的认证配置尚未保存。");
            StartupManager.ApplyElevatedMode(settings.ForceConnect);
            AppData.Log(settings.ForceConnect
                ? "已注册最高权限开机任务：" + StartupManager.TaskName
                : "已移除最高权限开机任务：" + StartupManager.TaskName);
        }
        catch (Exception ex)
        {
            AppData.Log("安装最高权限任务失败：" + ex.Message);
            Environment.ExitCode = 1;
        }
    }

}
