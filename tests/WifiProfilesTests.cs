using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

internal static class WifiProfilesTests
{
    static int failed;
    const BindingFlags InternalStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static Type Component(string name)
    {
        Type result = typeof(WifiProfilesTests).Assembly.GetType(name);
        if (result == null) throw new Exception("missing production component " + name);
        return result;
    }
    static object Call(string type, string method, params object[] arguments)
    {
        MethodInfo target = Component(type).GetMethod(method, InternalStatic);
        if (target == null) throw new Exception("missing production method " + type + "." + method);
        try { return target.Invoke(null, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static object Snapshot(string[] names, string connected, bool succeeded)
    {
        object result = Activator.CreateInstance(Component("WifiProfileSnapshot"), true);
        FieldInfo profiles = result.GetType().GetField("Profiles", BindingFlags.NonPublic | BindingFlags.Instance);
        profiles.SetValue(result, new List<string>(names));
        result.GetType().GetField("ConnectedProfile", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(result, connected);
        result.GetType().GetField("QuerySucceeded", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(result, succeeded);
        return result;
    }
    static string Choose(string saved, string[] names, string connected)
    { return (string)Call("WifiProfileChoices", "ChooseProfile", saved, Snapshot(names, connected, true)); }
    static void Assert(bool value, string message)
    { if (!value) throw new Exception(message); }
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static IntPtr Buffer(int bytes)
    {
        IntPtr result = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(new byte[bytes], 0, result, bytes);
        return result;
    }
    static void TextAt(IntPtr buffer, int offset, string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
        Marshal.Copy(bytes, 0, IntPtr.Add(buffer, offset), bytes.Length);
    }
    static object Network(string ssid, string profile, bool available, uint quality)
    {
        object result = Activator.CreateInstance(Component("WifiNetworkChoice"), true);
        foreach (var pair in new Dictionary<string, object> { { "Ssid", ssid }, { "ProfileName", profile }, { "IsAvailable", available }, { "SignalQuality", quality } })
            result.GetType().GetField(pair.Key, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(result, pair.Value);
        return result;
    }
    static object Field(object value, string name)
    { return value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
    static System.Collections.IList Choices(object snapshot)
    { return (System.Collections.IList)Call("WifiProfileChoices", "NetworkChoices", snapshot); }
    static void AddNetwork(object snapshot, object network)
    { ((System.Collections.IList)Field(snapshot, "Networks")).Add(network); }
    static void AvailableAt(IntPtr buffer, int offset, string profile, byte[] ssid, int flags, int bssids, int quality)
    {
        TextAt(buffer, offset, profile);
        Marshal.WriteInt32(buffer, offset + 512, ssid.Length);
        Marshal.Copy(ssid, 0, IntPtr.Add(buffer, offset + 516), ssid.Length);
        Marshal.WriteInt32(buffer, offset + 548, 1);
        Marshal.WriteInt32(buffer, offset + 552, bssids);
        Marshal.WriteInt32(buffer, offset + 556, 1);
        Marshal.WriteInt32(buffer, offset + 564, 1);
        Marshal.WriteInt32(buffer, offset + 568, 7);
        Marshal.WriteInt32(buffer, offset + 604, quality);
        Marshal.WriteInt32(buffer, offset + 608, 1);
        Marshal.WriteInt32(buffer, offset + 612, 6);
        Marshal.WriteInt32(buffer, offset + 616, 4);
        Marshal.WriteInt32(buffer, offset + 620, flags);
    }
    static bool InvalidBuffer(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }
    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Check("profile choices preserve Unicode and exact spaces while removing case duplicates", delegate {
            var actual = (List<string>)Call("WifiProfileChoices", "Normalize", (object)new string[] { null, "", "校园 Wi-Fi", "Campus Saved", "CAMPUS SAVED", "  edge spaces  ", "Home" });
            Assert(actual.Count == 4 && actual[0] == "校园 Wi-Fi" && actual[1] == "Campus Saved" && actual[2] == "  edge spaces  " && actual[3] == "Home", "profile identities were changed, invented, or duplicated");
        });
        Check("saved choice uses the spelling discovered on this host", delegate {
            Assert(Choose("campus saved", new[] { "Home", "Campus Saved" }, "Home") == "Campus Saved", "saved choice was not matched to a discovered profile");
        });
        Check("missing saved choice uses only a discovered current profile", delegate {
            Assert(Choose("Removed", new[] { "Home", "校园配置" }, "校园配置") == "校园配置", "discovered connection was not selected");
        });
        Check("missing saved choice never selects the first unrelated network", delegate {
            Assert(Choose("Removed", new[] { "Home", "Office" }, "SSID only") == "", "an unrelated or undiscovered network was selected");
            Assert(Choose("Removed", new string[0], "Removed") == "", "a stale configured profile was invented");
            Assert((string)Call("WifiProfileChoices", "ChooseProfile", "Removed", null) == "", "null snapshot resurrected a configured name");
        });
        Check("profile choices do not trim names into different profiles", delegate {
            Assert(Choose("Campus", new[] { " Campus " }, null) == "", "profile identity was trimmed");
            Assert(Choose(" Campus ", new[] { " Campus " }, null) == " Campus ", "saved spaces were lost");
        });
        Check("native profile list parser uses the header and 516 byte Unicode entries", delegate {
            IntPtr buffer = Buffer(8 + 3 * 516);
            try {
                Marshal.WriteInt32(buffer, 0, 3); Marshal.WriteInt32(buffer, 4, 2);
                TextAt(buffer, 8, "校园 保存配置"); Marshal.WriteInt32(buffer, 8 + 512, 3);
                TextAt(buffer, 8 + 516, "Office Network"); Marshal.WriteInt32(buffer, 8 + 516 + 512, 1);
                TextAt(buffer, 8 + 1032, new string('界', 255));
                var names = (List<string>)Call("WifiNativeProfileParser", "ReadProfiles", buffer);
                Assert(names.Count == 3 && names[0] == "校园 保存配置" && names[1] == "Office Network" && names[2] == new string('界', 255), "native profile offsets or UTF-16 width are wrong");
            } finally { Marshal.FreeHGlobal(buffer); }
        });
        Check("native interface parser reads all GUIDs using 532 byte entries", delegate {
            Guid first = new Guid("11111111-2222-3333-4444-555555555555"), second = new Guid("66666666-7777-8888-9999-aaaaaaaaaaaa");
            IntPtr buffer = Buffer(8 + 2 * 532);
            try {
                Marshal.WriteInt32(buffer, 0, 2);
                Marshal.Copy(first.ToByteArray(), 0, IntPtr.Add(buffer, 8), 16); TextAt(buffer, 8 + 16, "无线接口一"); Marshal.WriteInt32(buffer, 8 + 528, 1);
                Marshal.Copy(second.ToByteArray(), 0, IntPtr.Add(buffer, 8 + 532), 16); TextAt(buffer, 8 + 532 + 16, "Wireless adapter two"); Marshal.WriteInt32(buffer, 8 + 532 + 528, 4);
                var ids = (List<Guid>)Call("WifiNativeProfileParser", "ReadInterfaceIds", buffer);
                Assert(ids.Count == 2 && ids[0] == first && ids[1] == second, "interface stride or GUID parsing is wrong");
            } finally { Marshal.FreeHGlobal(buffer); }
        });
        Check("current connection parser reads the saved profile instead of the SSID", delegate {
            IntPtr buffer = Buffer(604);
            try {
                Marshal.WriteInt32(buffer, 0, 1); Marshal.WriteInt32(buffer, 4, 0);
                TextAt(buffer, 8, "已保存校园配置");
                byte[] ssid = Encoding.UTF8.GetBytes("Different broadcast SSID");
                Marshal.WriteInt32(buffer, 520, ssid.Length); Marshal.Copy(ssid, 0, IntPtr.Add(buffer, 524), ssid.Length);
                Assert((string)Call("WifiNativeProfileParser", "ReadConnectedProfile", buffer, 604) == "已保存校园配置", "broadcast SSID replaced the saved profile name");
                Marshal.WriteInt32(buffer, 0, 4);
                Assert(Call("WifiNativeProfileParser", "ReadConnectedProfile", buffer, 604) == null, "disconnected profile was considered current");
            } finally { Marshal.FreeHGlobal(buffer); }
        });
        Check("native parsers reject missing and implausible list buffers", delegate {
            Assert(InvalidBuffer(delegate { Call("WifiNativeProfileParser", "ReadProfiles", IntPtr.Zero); }), "missing native list was accepted");
            IntPtr buffer = Buffer(8);
            try {
                Marshal.WriteInt32(buffer, 0, -1);
                Assert(InvalidBuffer(delegate { Call("WifiNativeProfileParser", "ReadProfiles", buffer); }), "invalid profile count was accepted");
                Assert(InvalidBuffer(delegate { Call("WifiNativeProfileParser", "ReadInterfaceIds", buffer); }), "invalid interface count was accepted");
                Assert(InvalidBuffer(delegate { Call("WifiNativeProfileParser", "ReadConnectedProfile", buffer, 8); }), "truncated connection buffer was accepted");
            } finally { Marshal.FreeHGlobal(buffer); }
        });
        Check("an empty native profile list remains empty", delegate {
            IntPtr buffer = Buffer(8);
            try { Assert(((List<string>)Call("WifiNativeProfileParser", "ReadProfiles", buffer)).Count == 0, "empty list gained a fixed or configured profile"); }
            finally { Marshal.FreeHGlobal(buffer); }
        });
        Check("explicit search is separate from readonly profile discovery", delegate {
            Type contract = Component("IWifiSearchSource");
            Assert(Component("IWifiProfileSource").IsAssignableFrom(contract), "search contract does not include readonly discovery");
            Assert(contract.IsAssignableFrom(Component("WindowsWifiProfileSource")) && contract.GetMethod("Search") != null, "explicit scan capability is missing");
        });
        Check("nearby choices contain only available observations without historical profiles", delegate {
            object snapshot = Snapshot(new[] { "Old saved one", "Old saved two" }, "Old saved one", true);
            AddNetwork(snapshot, Network("校园附近", null, true, 77));
            AddNetwork(snapshot, Network("Gone", "Old saved one", false, 0));
            var choices = Choices(snapshot);
            Assert(choices.Count == 1 && (string)Field(choices[0], "Ssid") == "校园附近", "saved history or unavailable entries flooded the nearby list");
            Assert(Choices(null).Count == 0, "null discovery invented networks");
        });
        Check("nearby deduplication prefers saved mappings and keeps case-sensitive SSIDs", delegate {
            object snapshot = Snapshot(new[] { "Campus profile" }, null, true);
            AddNetwork(snapshot, Network("Campus", null, true, 95));
            AddNetwork(snapshot, Network("Campus", "Campus profile", true, 60));
            AddNetwork(snapshot, Network("campus", null, true, 70));
            var choices = Choices(snapshot);
            Assert(choices.Count == 2 && (string)Field(choices[0], "ProfileName") == "Campus profile" && (string)Field(choices[1], "Ssid") == "campus", "SSID deduplication lost a saved mapping or merged different SSIDs");
        });
        Check("nearby deduplication retains the currently connected saved mapping", delegate {
            object snapshot = Snapshot(new[] { "Alternate campus", "Current campus" }, "Current campus", true);
            AddNetwork(snapshot, Network("Campus", "Alternate campus", true, 95));
            AddNetwork(snapshot, Network("Campus", "Current campus", true, 60));
            var choices = Choices(snapshot);
            Assert(choices.Count == 1 && (string)Field(choices[0], "ProfileName") == "Current campus", "deduplication hid the actual connected profile");
        });
        Check("network labels identify saved, unsaved, and unavailable networks", delegate {
            object saved = Network("校园附近", "Saved campus", true, 80), unsaved = Network("Guest", null, true, 50), unavailable = Network("Old", "Old profile", false, 0);
            PropertyInfo savedProperty = Component("WifiNetworkChoice").GetProperty("IsSaved", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert(savedProperty != null && (bool)savedProperty.GetValue(saved, null) && !(bool)savedProperty.GetValue(unsaved, null), "SSID text was guessed to be a saved profile");
            Assert(saved.ToString().Contains("校园附近") && saved.ToString().Contains("已保存") && unsaved.ToString().Contains("未保存") && unavailable.ToString().Contains("不可用"), "network labels hide setup or availability state");
        });
        Check("native nearby parser uses 628 byte entries and verified saved-profile flags", delegate {
            IntPtr buffer = Buffer(8 + 3 * 628);
            try {
                Marshal.WriteInt32(buffer, 0, 3);
                AvailableAt(buffer, 8, "Saved campus", Encoding.UTF8.GetBytes("校园 SSID"), 2, 1, 84);
                AvailableAt(buffer, 8 + 628, "Unknown profile", Encoding.UTF8.GetBytes("Guest"), 2, 2, 67);
                AvailableAt(buffer, 8 + 1256, "Saved campus", Encoding.UTF8.GetBytes("SSID is not profile"), 0, 1, 42);
                var choices = (System.Collections.IList)Call("WifiNativeProfileParser", "ReadAvailableNetworks", buffer, new[] { "Saved campus" });
                Assert(choices.Count == 3 && (string)Field(choices[0], "Ssid") == "校园 SSID" && (string)Field(choices[0], "ProfileName") == "Saved campus" && (uint)Field(choices[0], "SignalQuality") == 84, "SSID, profile, or native stride was parsed incorrectly");
                Assert(String.IsNullOrEmpty((string)Field(choices[1], "ProfileName")) && String.IsNullOrEmpty((string)Field(choices[2], "ProfileName")), "unknown or unflagged profile mapping was invented");
            } finally { Marshal.FreeHGlobal(buffer); }
        });
        Check("native nearby parser excludes hidden and stale cached networks", delegate {
            IntPtr buffer = Buffer(8 + 3 * 628);
            try {
                Marshal.WriteInt32(buffer, 0, 3);
                AvailableAt(buffer, 8, "Saved campus", Encoding.UTF8.GetBytes("Out of range"), 2, 0, 0);
                AvailableAt(buffer, 8 + 628, "", new byte[0], 0, 1, 50);
                AvailableAt(buffer, 8 + 1256, "Saved campus", Encoding.UTF8.GetBytes("Connected"), 3, 0, 80);
                var choices = (System.Collections.IList)Call("WifiNativeProfileParser", "ReadAvailableNetworks", buffer, new[] { "Saved campus" });
                Assert(choices.Count == 1 && (string)Field(choices[0], "Ssid") == "Connected", "cached saved history or unnamed hidden network was shown as nearby");
            } finally { Marshal.FreeHGlobal(buffer); }
        });
        Check("SSID decoding respects byte length and handles non-UTF8 octets", delegate {
            IntPtr buffer = Buffer(8 + 628);
            try {
                Marshal.WriteInt32(buffer, 0, 1);
                AvailableAt(buffer, 8, "", new byte[] { 0xff, 0xfe, 0x41 }, 0, 1, 70);
                var choices = (System.Collections.IList)Call("WifiNativeProfileParser", "ReadAvailableNetworks", buffer, new string[0]);
                Assert(choices.Count == 1 && !String.IsNullOrEmpty((string)Field(choices[0], "Ssid")), "non-UTF8 SSID crashed discovery");
                Marshal.WriteInt32(buffer, 8 + 512, 33);
                Assert(InvalidBuffer(delegate { Call("WifiNativeProfileParser", "ReadAvailableNetworks", buffer, new string[0]); }), "SSID length exceeded the native 32-byte buffer");
            } finally { Marshal.FreeHGlobal(buffer); }
        });
        if (Environment.GetEnvironmentVariable("CAMPUSFLOW_WIFI_NATIVE_SMOKE") == "1")
        {
            Check("readonly Windows discovery reports its actual result", delegate {
                object source = Activator.CreateInstance(Component("WindowsWifiProfileSource"), true);
                object result = source.GetType().GetMethod("Read").Invoke(source, null);
                var profiles = (List<string>)result.GetType().GetField("Profiles", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                bool succeeded = (bool)result.GetType().GetField("QuerySucceeded", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                string notice = (string)result.GetType().GetField("Notice", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                string current = (string)result.GetType().GetField("ConnectedProfile", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(result);
                Assert(profiles != null && (succeeded || !String.IsNullOrWhiteSpace(notice)), "failed discovery did not explain its result");
                Assert(current == null || profiles.Contains(current), "current profile was not discovered");
                var networks = (System.Collections.IList)Field(result, "Networks");
                foreach (object network in networks)
                {
                    string profile = (string)Field(network, "ProfileName");
                    Assert((bool)Field(network, "IsAvailable") && !String.IsNullOrEmpty((string)Field(network, "Ssid")), "cached discovery returned an unavailable or unnamed choice");
                    Assert(String.IsNullOrEmpty(profile) || profiles.Contains(profile), "nearby choice guessed an undiscovered profile");
                }
                Console.WriteLine("INFO Native readonly discovery: success=" + succeeded + "; profiles=" + profiles.Count + "; nearby=" + networks.Count + "; hasCurrent=" + (current != null) + "; notice=" + notice);
            });
        }
        return failed == 0 ? 0 : 1;
    }
}
