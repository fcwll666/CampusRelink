using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal interface IWifiProfileSource
{
    WifiProfileSnapshot Read();
}

internal interface IWifiSearchSource : IWifiProfileSource
{
    WifiProfileSnapshot Search();
}

internal sealed class WifiNetworkChoice
{
    internal string Ssid;
    internal string ProfileName;
    internal bool IsAvailable = true;
    internal uint SignalQuality;
    internal bool IsSaved { get { return !String.IsNullOrEmpty(ProfileName); } }

    public override string ToString()
    {
        string status = IsSaved ? "已保存" : "未保存";
        status += IsAvailable ? "，信号 " + SignalQuality + "%" : "，当前不可用";
        return (Ssid ?? "") + "（" + status + "）";
    }
}

internal sealed class WifiProfileSnapshot
{
    internal List<string> Profiles = new List<string>();
    internal List<WifiNetworkChoice> Networks = new List<WifiNetworkChoice>();
    internal string ConnectedProfile;
    internal bool QuerySucceeded;
    internal string Notice;
}

internal static class WifiProfileChoices
{
    // A profile name is a Windows identity: preserve its Unicode and spaces.
    internal static List<string> Normalize(IEnumerable<string> profiles)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profiles == null) return result;
        foreach (string profile in profiles)
            if (!String.IsNullOrEmpty(profile) && seen.Add(profile)) result.Add(profile);
        return result;
    }

    internal static string ChooseProfile(string savedSelection, WifiProfileSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Profiles == null) return "";
        string saved = Find(snapshot.Profiles, savedSelection);
        if (saved != null) return saved;
        return Find(snapshot.Profiles, snapshot.ConnectedProfile) ?? "";
    }

    internal static List<WifiNetworkChoice> NetworkChoices(WifiProfileSnapshot snapshot)
    {
        var choices = new List<WifiNetworkChoice>();
        if (snapshot == null || snapshot.Networks == null) return choices;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (WifiNetworkChoice network in snapshot.Networks)
        {
            if (network == null || !network.IsAvailable || String.IsNullOrEmpty(network.Ssid)) continue;
            var candidate = new WifiNetworkChoice {
                Ssid = network.Ssid,
                ProfileName = Find(snapshot.Profiles, network.ProfileName),
                IsAvailable = true,
                SignalQuality = Math.Min(100u, network.SignalQuality)
            };
            int index;
            if (!indexes.TryGetValue(candidate.Ssid, out index))
            {
                indexes.Add(candidate.Ssid, choices.Count);
                choices.Add(candidate);
            }
            else
            {
                WifiNetworkChoice previous = choices[index];
                bool candidateIsCurrent = candidate.IsSaved && String.Equals(candidate.ProfileName, snapshot.ConnectedProfile, StringComparison.OrdinalIgnoreCase);
                bool previousIsCurrent = previous.IsSaved && String.Equals(previous.ProfileName, snapshot.ConnectedProfile, StringComparison.OrdinalIgnoreCase);
                if ((candidateIsCurrent && !previousIsCurrent)
                    || (candidateIsCurrent == previousIsCurrent && ((candidate.IsSaved && !previous.IsSaved)
                        || (candidate.IsSaved == previous.IsSaved && candidate.SignalQuality > previous.SignalQuality))))
                    choices[index] = candidate;
            }
        }
        return choices;
    }

    internal static string Find(IEnumerable<string> profiles, string name)
    {
        if (profiles == null || String.IsNullOrEmpty(name)) return null;
        foreach (string profile in profiles)
            if (String.Equals(profile, name, StringComparison.OrdinalIgnoreCase)) return profile;
        return null;
    }
}

// These parsers consume the same native buffers returned by wlanapi.dll. The
// marshalled entry sizes determine strides; only the documented fixed arrays
// are represented here, so the layout is the same in x86 and x64 processes.
internal static class WifiNativeProfileParser
{
    [StructLayout(LayoutKind.Sequential)]
    struct ListHeader
    {
        internal uint Count;
        internal uint Index;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Name;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct InterfaceInfo
    {
        internal Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Description;
        internal uint State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ConnectionPrefix
    {
        internal uint State;
        internal uint ConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string ProfileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Dot11Ssid
    {
        internal uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct AvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string ProfileName;
        internal Dot11Ssid Ssid;
        internal uint BssType;
        internal uint NumberOfBssids;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool NetworkConnectable;
        internal uint NotConnectableReason;
        internal uint NumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        internal uint[] PhyTypes;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool MorePhyTypes;
        internal uint SignalQuality;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool SecurityEnabled;
        internal uint DefaultAuthAlgorithm;
        internal uint DefaultCipherAlgorithm;
        internal uint Flags;
        internal uint Reserved;
    }

    static int Count(IntPtr buffer)
    {
        if (buffer == IntPtr.Zero) throw new InvalidDataException("Windows WLAN 返回了空列表。");
        ListHeader header = (ListHeader)Marshal.PtrToStructure(buffer, typeof(ListHeader));
        // Bound pointer arithmetic and allocation if an invalid native result is received.
        if (header.Count > 4096) throw new InvalidDataException("Windows WLAN 列表长度无效。");
        return (int)header.Count;
    }

    internal static List<string> ReadProfiles(IntPtr buffer)
    {
        int count = Count(buffer);
        int headerSize = Marshal.SizeOf(typeof(ListHeader));
        int entrySize = Marshal.SizeOf(typeof(ProfileInfo));
        var profiles = new List<string>();
        for (int index = 0; index < count; index++)
        {
            IntPtr item = IntPtr.Add(buffer, checked(headerSize + index * entrySize));
            ProfileInfo profile = (ProfileInfo)Marshal.PtrToStructure(item, typeof(ProfileInfo));
            if (!String.IsNullOrEmpty(profile.Name)) profiles.Add(profile.Name);
        }
        return profiles;
    }

    internal static List<Guid> ReadInterfaceIds(IntPtr buffer)
    {
        int count = Count(buffer);
        int headerSize = Marshal.SizeOf(typeof(ListHeader));
        int entrySize = Marshal.SizeOf(typeof(InterfaceInfo));
        var interfaces = new List<Guid>();
        for (int index = 0; index < count; index++)
        {
            IntPtr item = IntPtr.Add(buffer, checked(headerSize + index * entrySize));
            InterfaceInfo info = (InterfaceInfo)Marshal.PtrToStructure(item, typeof(InterfaceInfo));
            interfaces.Add(info.Id);
        }
        return interfaces;
    }

    internal static List<WifiNetworkChoice> ReadAvailableNetworks(IntPtr buffer, IEnumerable<string> savedProfiles)
    {
        int count = Count(buffer);
        int headerSize = Marshal.SizeOf(typeof(ListHeader));
        int entrySize = Marshal.SizeOf(typeof(AvailableNetwork));
        var networks = new List<WifiNetworkChoice>();
        for (int index = 0; index < count; index++)
        {
            IntPtr item = IntPtr.Add(buffer, checked(headerSize + index * entrySize));
            AvailableNetwork network = (AvailableNetwork)Marshal.PtrToStructure(item, typeof(AvailableNetwork));
            if (network.Ssid.Length > 32) throw new InvalidDataException("Windows WLAN SSID 字节长度无效。");
            bool available = network.NumberOfBssids > 0 || (network.Flags & 1) != 0;
            if (!available || network.Ssid.Length == 0) continue;
            string ssid;
            try { ssid = new UTF8Encoding(false, true).GetString(network.Ssid.Bytes, 0, (int)network.Ssid.Length); }
            catch (DecoderFallbackException) { ssid = Encoding.Default.GetString(network.Ssid.Bytes, 0, (int)network.Ssid.Length); }
            networks.Add(new WifiNetworkChoice {
                Ssid = ssid,
                ProfileName = (network.Flags & 2) != 0 ? WifiProfileChoices.Find(savedProfiles, network.ProfileName) : null,
                IsAvailable = true,
                SignalQuality = Math.Min(100u, network.SignalQuality)
            });
        }
        return networks;
    }

    internal static string ReadConnectedProfile(IntPtr buffer, int bytes)
    {
        if (buffer == IntPtr.Zero || bytes < Marshal.SizeOf(typeof(ConnectionPrefix)))
            throw new InvalidDataException("Windows WLAN 当前连接数据不完整。");
        ConnectionPrefix connection = (ConnectionPrefix)Marshal.PtrToStructure(buffer, typeof(ConnectionPrefix));
        // Connected or an established ad-hoc network. Other states are not current links.
        if (connection.State != 1 && connection.State != 2) return null;
        return String.IsNullOrEmpty(connection.ProfileName) ? null : connection.ProfileName;
    }
}

// Read queries cached Windows WLAN state. Search requests a scan only when the
// UI explicitly calls it. Neither path connects or changes saved profiles.
internal sealed class WindowsWifiProfileSource : IWifiSearchSource
{
    public WifiProfileSnapshot Read() { return ReadCore(false); }
    public WifiProfileSnapshot Search() { return ReadCore(true); }

    WifiProfileSnapshot ReadCore(bool requestScan)
    {
        var snapshot = new WifiProfileSnapshot();
        IntPtr client = IntPtr.Zero;
        IntPtr interfaceList = IntPtr.Zero;
        try
        {
            uint negotiated;
            uint status = NativeMethods.WlanOpenHandle(2, IntPtr.Zero, out negotiated, out client);
            if (status != 0)
            {
                snapshot.Notice = Failure("无法读取 Windows Wi-Fi 配置", status);
                return snapshot;
            }
            status = NativeMethods.WlanEnumInterfaces(client, IntPtr.Zero, out interfaceList);
            if (status != 0)
            {
                snapshot.Notice = Failure("无法枚举无线网卡", status);
                return snapshot;
            }
            List<Guid> interfaces = WifiNativeProfileParser.ReadInterfaceIds(interfaceList);
            if (interfaces.Count == 0)
            {
                snapshot.QuerySucceeded = true;
                snapshot.Notice = "未检测到无线网卡；请检查 Windows 无线设备。";
                return snapshot;
            }

            bool complete = true;
            bool scanAccepted = false;
            var failures = new List<string>();
            if (requestScan)
            {
                foreach (Guid id in interfaces)
                {
                    Guid interfaceId = id;
                    status = NativeMethods.WlanScan(client, ref interfaceId, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (status == 0) scanAccepted = true;
                    else { complete = false; failures.Add(Failure("Windows Wi-Fi 扫描请求失败", status)); }
                }
                // No completion notification is assumed. Give Windows a bounded
                // two seconds to update its cache, then query the observed results.
                if (scanAccepted) Thread.Sleep(2000);
            }
            var connectedProfiles = new List<string>();
            foreach (Guid id in interfaces)
            {
                Guid interfaceId = id;
                IntPtr profileList = IntPtr.Zero;
                try
                {
                    status = NativeMethods.WlanGetProfileList(client, ref interfaceId, IntPtr.Zero, out profileList);
                    if (status == 0) snapshot.Profiles.AddRange(WifiNativeProfileParser.ReadProfiles(profileList));
                    else
                    {
                        complete = false;
                        failures.Add(Failure("某个无线网卡的已保存配置不可读取", status));
                    }
                }
                finally { if (profileList != IntPtr.Zero) NativeMethods.WlanFreeMemory(profileList); }

                IntPtr availableList = IntPtr.Zero;
                try
                {
                    status = NativeMethods.WlanGetAvailableNetworkList(client, ref interfaceId, 0, IntPtr.Zero, out availableList);
                    if (status == 0) snapshot.Networks.AddRange(WifiNativeProfileParser.ReadAvailableNetworks(availableList, snapshot.Profiles));
                    else { complete = false; failures.Add(Failure("附近 Wi-Fi 列表暂不可读取，请检查 WLAN 服务及 Windows 位置权限", status)); }
                }
                finally { if (availableList != IntPtr.Zero) NativeMethods.WlanFreeMemory(availableList); }

                IntPtr connectionData = IntPtr.Zero;
                try
                {
                    uint dataSize;
                    int valueType;
                    status = NativeMethods.WlanQueryInterface(client, ref interfaceId, 7, IntPtr.Zero, out dataSize, out connectionData, out valueType);
                    if (status == 0)
                    {
                        if (dataSize > Int32.MaxValue) throw new InvalidDataException("Windows WLAN 当前连接数据长度无效。");
                        string current = WifiNativeProfileParser.ReadConnectedProfile(connectionData, (int)dataSize);
                        if (current != null) connectedProfiles.Add(current);
                    }
                    // ERROR_INVALID_STATE and ERROR_NOT_FOUND are normal when disconnected.
                    else if (status != 5023 && status != 1168)
                        failures.Add(Failure("当前 Wi-Fi 连接状态暂不可读取", status));
                }
                finally { if (connectionData != IntPtr.Zero) NativeMethods.WlanFreeMemory(connectionData); }
            }

            snapshot.Profiles = WifiProfileChoices.Normalize(snapshot.Profiles);
            foreach (string connected in connectedProfiles)
            {
                string found = WifiProfileChoices.Find(snapshot.Profiles, connected);
                if (found != null) { snapshot.ConnectedProfile = found; break; }
            }
            snapshot.Networks = WifiProfileChoices.NetworkChoices(snapshot);
            snapshot.QuerySucceeded = complete;
            snapshot.Notice = "已读取 " + snapshot.Networks.Count + " 个附近 Wi-Fi、" + snapshot.Profiles.Count + " 个已保存配置。";
            if (requestScan && scanAccepted)
                snapshot.Notice += " 扫描请求已提交；Windows 可能仍在更新结果，可稍后再次搜索。";
            if (snapshot.Networks.Count == 0)
                snapshot.Notice += " 暂未发现可显示的附近网络，请检查无线开关后重新搜索。";
            if (snapshot.Profiles.Count == 0)
                snapshot.Notice += " 请先在 Windows 中连接并保存要使用的网络。";
            if (failures.Count > 0) snapshot.Notice += " " + String.Join(" ", failures.ToArray());
            return snapshot;
        }
        catch (DllNotFoundException)
        {
            snapshot.Notice = "当前系统未提供 Windows WLAN 服务，无法读取 Wi-Fi 配置。";
        }
        catch (EntryPointNotFoundException)
        {
            snapshot.Notice = "当前系统不支持所需的 Windows WLAN 查询。";
        }
        catch (Exception ex)
        {
            snapshot.Notice = "读取 Windows Wi-Fi 配置失败：" + ex.Message;
        }
        finally
        {
            if (interfaceList != IntPtr.Zero) NativeMethods.WlanFreeMemory(interfaceList);
            if (client != IntPtr.Zero) NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
        }
        snapshot.Profiles = WifiProfileChoices.Normalize(snapshot.Profiles);
        snapshot.Networks = WifiProfileChoices.NetworkChoices(snapshot);
        snapshot.QuerySucceeded = false;
        return snapshot;
    }

    static string Failure(string action, uint error)
    {
        return action + "（Windows 错误 " + error + "：" + new Win32Exception(unchecked((int)error)).Message + "）。";
    }

    static class NativeMethods
    {
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceId, IntPtr reserved, out IntPtr profileList);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceId, uint flags, IntPtr reserved, out IntPtr availableNetworkList);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceId, IntPtr dot11Ssid, IntPtr informationElements, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceId, int opcode, IntPtr reserved, out uint dataSize, out IntPtr data, out int opcodeValueType);
        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr memory);
    }
}
