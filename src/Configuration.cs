using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

public sealed class PortalSettings
{
    public int CheckIntervalSeconds { get; set; }
    public bool NightMode { get; set; }
    public bool ForceConnect { get; set; }
    public bool RepairStartupProxy { get; set; }
    bool autoConnectWifi;
    bool autoConnectWifiSpecified;
    public bool AutoConnectWifi
    {
        get { return autoConnectWifi; }
        set { autoConnectWifi = value; autoConnectWifiSpecified = true; }
    }
    public string WifiProfile { get; set; }
    public string LoginUrl { get; set; }
    [XmlIgnore]
    public string PortalUrl { get; set; }
    [XmlIgnore]
    public string RequestEncoding { get; set; }
    [XmlIgnore]
    public string UsernameField { get; set; }
    [XmlIgnore]
    public string PasswordField { get; set; }
    [XmlIgnore]
    public string ExtraFields { get; set; }
    [XmlIgnore]
    public string SuccessField { get; set; }
    [XmlIgnore]
    public string SuccessValue { get; set; }
    [XmlIgnore]
    public string ConnectivityUrl { get; set; }
    [XmlIgnore]
    public string ConnectivityExpected { get; set; }
    public PortalSettings()
    {
        CheckIntervalSeconds = 30;
        NightMode = true;
        RepairStartupProxy = true;
        WifiProfile = PortalUrl = ExtraFields = "";
        LoginUrl = BuiltInAuthentication.DefaultLoginUrl;
        RequestEncoding = "Json";
        UsernameField = "username";
        PasswordField = "password";
        SuccessField = "reply_code";
        SuccessValue = "0";
        ConnectivityUrl = "http://www.msftconnecttest.com/connecttest.txt";
        ConnectivityExpected = "Microsoft Connect Test";
        BuiltInAuthentication.Apply(this);
    }

    public static PortalSettings Default() { return new PortalSettings(); }

    internal string Validate()
    {
        if (CheckIntervalSeconds < 20 || CheckIntervalSeconds > 3600) return "检查间隔必须为 20 至 3600 秒。";
        if (!SafeUrl(LoginUrl)) return "请填写有效的 HTTP 或 HTTPS 登录地址，地址不能包含账户信息或片段。";
        if (!String.IsNullOrWhiteSpace(PortalUrl) && !SafeUrl(PortalUrl)) return "门户页面地址无效，地址不能包含账户信息或片段。";
        if (!SafeUrl(ConnectivityUrl)) return "联网检测地址必须是有效的 HTTP 或 HTTPS 地址，不能包含账户信息或片段。";
        if (String.IsNullOrWhiteSpace(ConnectivityExpected)) return "联网检测期望响应不能为空。";
        if (AutoConnectWifi && String.IsNullOrWhiteSpace(WifiProfile)) return "开启自动连接 Wi-Fi 后，请从本机网络列表中选择校园网。";
        if (ForceConnect && !AutoConnectWifi) return "强连接模式需要先开启自动连接 Wi-Fi。";
        foreach (char character in WifiProfile ?? "")
            if (character == '"' || Char.IsControl(character) || character == '\u2028' || character == '\u2029') return "Wi-Fi 配置名不能包含双引号、换行或控制字符。";
        if (!ValidField(UsernameField) || !ValidField(PasswordField)) return "用户名和密码字段名不能为空或包含换行。";
        if (String.Equals(UsernameField, PasswordField, StringComparison.OrdinalIgnoreCase)) return "用户名和密码字段名必须不同。";
        if (RequestEncoding != "Json" && RequestEncoding != "Form") return "请求编码只能选择 Json 或 Form。";
        if (String.IsNullOrWhiteSpace(SuccessField)) return "成功字段不能为空。";
        string[] path = SuccessField.Split('.');
        foreach (string part in path) if (!ValidField(part)) return "成功字段路径无效。";
        if (SuccessValue == null) return "成功值不能为空值。";
        Dictionary<string, string> extras;
        return TryExtraFields(out extras);
    }

    bool SafeUrl(string text)
    {
        if (String.IsNullOrWhiteSpace(text) || text != text.Trim() || text.IndexOfAny(new[] { '\r', '\n', '#' }) >= 0) return false;
        Uri uri;
        if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || String.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0) return false;
        int authorityStart = text.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return false;
        authorityStart += 3;
        int authorityEnd = text.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
        if (authorityEnd < 0) authorityEnd = text.Length;
        if (text.Substring(authorityStart, authorityEnd - authorityStart).IndexOf('@') >= 0) return false;
        try
        {
            if (Uri.UnescapeDataString(text).IndexOfAny(new[] { '\r', '\n' }) >= 0) return false;
            foreach (string pair in uri.Query.TrimStart('?').Split(new[] { '&', ';' }))
            {
                string key = Uri.UnescapeDataString(pair.Split('=')[0].Replace('+', ' '));
                if (String.Equals(key, "username", StringComparison.OrdinalIgnoreCase) || String.Equals(key, "password", StringComparison.OrdinalIgnoreCase) || String.Equals(key, UsernameField, StringComparison.OrdinalIgnoreCase) || String.Equals(key, PasswordField, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        catch (UriFormatException) { return false; }
        return true;
    }

    static bool ValidField(string value)
    {
        return !String.IsNullOrWhiteSpace(value) && value == value.Trim() && value.IndexOfAny(new[] { '\r', '\n' }) < 0;
    }

    // Parse once for validation and once when composing a request. The same rule
    // prevents extra fields from replacing account values in either encoding.
    internal string TryExtraFields(out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in (ExtraFields ?? "").Split('\n'))
        {
            string line = rawLine.EndsWith("\r", StringComparison.Ordinal) ? rawLine.Substring(0, rawLine.Length - 1) : rawLine;
            if (line.IndexOf('\r') >= 0) return "附加字段不能包含回车注入。";
            if (String.IsNullOrWhiteSpace(line)) continue;
            int equals = line.IndexOf('=');
            if (equals < 1) return "附加字段必须每行使用 名称=值 格式。";
            string key = line.Substring(0, equals).Trim();
            if (!ValidField(key)) return "附加字段名称无效。";
            if (String.Equals(key, UsernameField, StringComparison.OrdinalIgnoreCase) || String.Equals(key, PasswordField, StringComparison.OrdinalIgnoreCase)) return "附加字段不能覆盖用户名或密码字段。";
            if (fields.ContainsKey(key)) return "附加字段名称不能重复。";
            fields.Add(key, line.Substring(equals + 1));
        }
        return null;
    }

    internal static PortalSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default();
            PortalSettings settings;
            using (FileStream input = File.OpenRead(path)) settings = (PortalSettings)ConfigurationXml.Read(typeof(PortalSettings), input) ?? Default();
            if (String.IsNullOrWhiteSpace(settings.LoginUrl)) settings.LoginUrl = BuiltInAuthentication.DefaultLoginUrl;
            if (!settings.autoConnectWifiSpecified) settings.AutoConnectWifi = !String.IsNullOrWhiteSpace(settings.WifiProfile);
            if (!settings.AutoConnectWifi) settings.ForceConnect = false;
            BuiltInAuthentication.Apply(settings);
            return settings;
        }
        catch (Exception) { return Default(); }
    }

    internal static void Save(string path, PortalSettings settings)
    {
        if (settings == null) throw new ArgumentNullException("settings");
        BuiltInAuthentication.Apply(settings);
        using (MemoryStream memory = new MemoryStream())
        {
            new XmlSerializer(typeof(PortalSettings)).Serialize(memory, settings);
            ConfigurationXml.Write(path, memory.ToArray());
        }
    }
}

public sealed class CredentialAccount
{
    public string Username { get; set; }
    public string Password { get; set; }
    public string BoundLoginUrl { get; set; }
    public CredentialAccount() { Username = Password = BoundLoginUrl = ""; }
    public bool IsComplete { get { return !String.IsNullOrWhiteSpace(Username) && !String.IsNullOrEmpty(Password); } }
}
static class CredentialStore
{
    internal static CredentialAccount Load() { return Load(AppData.CredentialPath); }
    internal static void Save(CredentialAccount value) { Save(AppData.CredentialPath, value); }
    internal static CredentialAccount Load(string path)
    {
        byte[] plain = null;
        try
        {
            if (!File.Exists(path)) return new CredentialAccount();
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            using (MemoryStream memory = new MemoryStream(plain, false))
            {
                return (CredentialAccount)ConfigurationXml.Read(typeof(CredentialAccount), memory) ?? new CredentialAccount();
            }
        }
        catch (Exception) { return new CredentialAccount(); }
        finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
    }

    internal static void Save(string path, CredentialAccount value)
    {
        if (value == null) throw new ArgumentNullException("value");
        byte[] plain = null;
        try
        {
            using (MemoryStream memory = new MemoryStream())
            {
                new XmlSerializer(typeof(CredentialAccount)).Serialize(memory, value);
                plain = memory.ToArray();
            }
            ConfigurationXml.Write(path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
    }
}
static class CredentialEditor
{
    internal static CredentialAccount Build(CredentialAccount existing, string username, string password)
    {
        username = (username ?? "").Trim();
        password = password ?? "";
        if (String.IsNullOrWhiteSpace(username)) throw new ArgumentException("用户名不能为空。");
        if (password.Length == 0 && existing != null && String.Equals(username, existing.Username, StringComparison.Ordinal)) password = existing.Password;
        if (String.IsNullOrEmpty(password)) throw new ArgumentException("需要填写密码；更换用户名后不能沿用旧密码。");
        return new CredentialAccount {
            Username = username,
            Password = password,
            BoundLoginUrl = existing != null && String.Equals(username, existing.Username, StringComparison.Ordinal) ? (existing.BoundLoginUrl ?? "") : ""
        };
    }
}

static class ConfigurationXml
{
    internal static object Read(Type type, Stream input)
    {
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using (XmlReader reader = XmlReader.Create(input, settings)) return new XmlSerializer(type).Deserialize(reader);
    }
    internal static void Write(string path, byte[] bytes)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
