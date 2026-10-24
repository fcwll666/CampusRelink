using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;

static class CommunityCoreTests
{
    static int failures;
    static string dataDirectory;

    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message); }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Throws(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Expected an argument validation error");
    }
    static PortalSettings Settings()
    {
        PortalSettings value = PortalSettings.Default();
        value.LoginUrl = "https://portal.example.invalid/api/login";
        return value;
    }
    static CredentialAccount Credentials()
    {
        return new CredentialAccount { Username = "dummy-user", Password = "dummy-pass" };
    }
    static void Invalid(Action<PortalSettings> edit)
    {
        PortalSettings settings = Settings(); edit(settings);
        Assert(!String.IsNullOrWhiteSpace(settings.Validate()), "Unsafe configuration accepted");
    }

    public static int Main()
    {
        dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testdata", "core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        Check("defaults prefill NJUCM authentication without an account or Wi-Fi profile", delegate {
            PortalSettings s = PortalSettings.Default();
            Assert(s.CheckIntervalSeconds == 30 && s.NightMode && !s.ForceConnect && s.RepairStartupProxy, "Unexpected safe defaults");
            Assert(s.LoginUrl == "http://net.njucm.edu.cn/api/portal/v1/login"
                && s.PortalUrl == "http://net.njucm.edu.cn/portal/index.html" && s.WifiProfile == "", "NJUCM defaults missing or Wi-Fi hardcoded");
            Assert(s.RequestEncoding == "Json" && s.UsernameField == "username" && s.PasswordField == "password" && s.ExtraFields == "domain=default", "Unexpected protocol defaults");
            Assert(s.SuccessField == "reply_code" && s.SuccessValue == "0", "Unexpected response defaults");
            Assert(s.ConnectivityUrl == "http://www.msftconnecttest.com/connecttest.txt" && s.ConnectivityExpected == "Microsoft Connect Test", "Unexpected connectivity defaults");
            Assert(s.Validate() == null && Settings().Validate() == null && !new CredentialAccount().IsComplete, "First-run credentials gate incorrect");
        });
        Check("custom authentication URLs survive save and reload", delegate {
            PortalSettings s = Settings();
            string path = Path.Combine(dataDirectory, "custom-portal.xml");
            PortalSettings.Save(path, s);
            Assert(PortalSettings.Load(path).LoginUrl == s.LoginUrl, "NJUCM default overwrote the user's custom endpoint");
            Assert(PortalSettings.Load(Path.Combine(dataDirectory, "missing-settings.xml")).LoginUrl == "http://net.njucm.edu.cn/api/portal/v1/login", "new installation did not use the school default");
        });
        Check("built-in connectivity keeps the original HTTP probe after apply save and reload", delegate {
            PortalSettings s = Settings(); s.ConnectivityUrl = "https://changed.example.invalid/check";
            BuiltInAuthentication.Apply(s);
            Assert(s.ConnectivityUrl == "http://www.msftconnecttest.com/connecttest.txt", "working HTTP connectivity endpoint changed");
            string path = Path.Combine(dataDirectory, "connectivity-settings.xml");
            PortalSettings.Save(path, s);
            Assert(PortalSettings.Load(path).ConnectivityUrl == "http://www.msftconnecttest.com/connecttest.txt",
                "persistence restores the broken HTTPS connectivity endpoint");
        });
        Check("settings accept bounded intervals and HTTP protocols", delegate {
            PortalSettings s = Settings(); s.CheckIntervalSeconds = 20; s.LoginUrl = "http://portal.example.invalid/login"; Assert(s.Validate() == null, "Lower bound rejected");
            s.CheckIntervalSeconds = 3600; s.PortalUrl = "https://portal.example.invalid/"; Assert(s.Validate() == null, "Upper bound rejected");
            Invalid(delegate(PortalSettings x) { x.CheckIntervalSeconds = 19; });
            Invalid(delegate(PortalSettings x) { x.CheckIntervalSeconds = 3601; });
        });
        Check("settings reject unsafe portal and connectivity URLs", delegate {
            string[] bad = { "", "/login", "ftp://portal.example.invalid/login", "https://dummy:dummy@portal.example.invalid/login", "https://portal.example.invalid/login#fragment", "https://portal.example.invalid/login?username=dummy", "https://portal.example.invalid/login?%70assword=dummy", "https://portal.example.invalid/\r\ninjected" };
            foreach (string url in bad) Invalid(delegate(PortalSettings x) { x.LoginUrl = url; });
            Invalid(delegate(PortalSettings x) { x.PortalUrl = "file:///test"; });
            Invalid(delegate(PortalSettings x) { x.ConnectivityUrl = "/probe"; });
            Invalid(delegate(PortalSettings x) { x.ConnectivityUrl = "https://dummy@probe.example.invalid/"; });
            Invalid(delegate(PortalSettings x) { x.UsernameField = "account"; x.LoginUrl += "?account=dummy"; });
        });
        Check("settings reject conflicting fields and unknown encodings", delegate {
            Invalid(delegate(PortalSettings x) { x.UsernameField = " "; });
            Invalid(delegate(PortalSettings x) { x.PasswordField = null; });
            Invalid(delegate(PortalSettings x) { x.PasswordField = "USERNAME"; });
            Invalid(delegate(PortalSettings x) { x.UsernameField = "u\r\nheader"; });
            Invalid(delegate(PortalSettings x) { x.RequestEncoding = "Xml"; });
            PortalSettings s = Settings(); s.RequestEncoding = "Form"; Assert(s.Validate() == null, "Form rejected");
        });
        Check("settings require nonblank connectivity response expectation", delegate {
            string[] invalid = { null, "", " ", "\t\r\n", "\u3000" };
            foreach (string expected in invalid) Invalid(delegate(PortalSettings x) { x.ConnectivityExpected = expected; });
            PortalSettings s = Settings(); s.ConnectivityExpected = "  demo online  ";
            Assert(s.Validate() == null, "Nonblank exact-match response rejected");
        });
        Check("wifi profile rejects command quotes and control characters", delegate {
            string[] invalid = { "Demo\" name=other", "Demo\rOther", "Demo\nOther", "Demo\tOther", "Demo\0Other", "Demo\u001fOther", "Demo\u007fOther", "Demo\u0085Other" };
            foreach (string profile in invalid) Invalid(delegate(PortalSettings x) { x.WifiProfile = profile; });
            string[] valid = { null, "", "Demo WiFi", "校园 网络" };
            foreach (string profile in valid) { PortalSettings s = Settings(); s.WifiProfile = profile; Assert(s.Validate() == null, "Ordinary or optional WiFi profile rejected"); }
        });
        Check("extra fields allow CRLF and equals in values", delegate {
            PortalSettings s = Settings(); s.ExtraFields = "tenant=demo\r\n\r\nreturn=a=b\n";
            Assert(s.Validate() == null, "Normal multiline extras rejected");
        });
        Check("extra fields reject malformed keys duplicate and account overrides", delegate {
            string[] bad = { "flag", "=value", "tenant=one\ntenant=two", "username=other", "PASSWORD=other", "tenant=one\rheader=value", "tenant=one\r\r\nheader=value" };
            foreach (string extra in bad) Invalid(delegate(PortalSettings x) { x.ExtraFields = extra; });
            Invalid(delegate(PortalSettings x) { x.UsernameField = "account"; x.ExtraFields = "account=other"; });
        });
        Check("settings roundtrip excludes account secrets", delegate {
            string path = Path.Combine(dataDirectory, "settings.xml");
            PortalSettings s = Settings(); s.RequestEncoding = "Form"; s.WifiProfile = "Demo WiFi"; s.AutoConnectWifi = true; s.ForceConnect = true; s.RepairStartupProxy = false; s.ExtraFields = "tenant=demo";
            PortalSettings.Save(path, s); PortalSettings actual = PortalSettings.Load(path);
            Assert(actual.Validate() == null && actual.LoginUrl == s.LoginUrl && actual.RequestEncoding == "Json" && actual.WifiProfile == "Demo WiFi" && actual.AutoConnectWifi && actual.ForceConnect && !actual.RepairStartupProxy && actual.ExtraFields == "domain=default", "Simple settings or built-in protocol did not roundtrip");
            string xml = File.ReadAllText(path); Assert(!xml.Contains("<Username>") && !xml.Contains("<Password>"), "Credential properties serialized in settings");
        });
        Check("settings missing and malformed files use fresh defaults", delegate {
            string path = Path.Combine(dataDirectory, "missing-settings.xml");
            PortalSettings a = PortalSettings.Load(path); a.LoginUrl = "https://changed.example.invalid";
            Assert(PortalSettings.Load(path).LoginUrl == "http://net.njucm.edu.cn/api/portal/v1/login", "Missing-file defaults were shared");
            File.WriteAllText(path, "<not-xml"); Assert(PortalSettings.Load(path).LoginUrl == "http://net.njucm.edu.cn/api/portal/v1/login", "Malformed settings did not fail safely");
            File.WriteAllText(path, "<PortalSettings><LoginUrl /></PortalSettings>");
            Assert(PortalSettings.Load(path).LoginUrl == "http://net.njucm.edu.cn/api/portal/v1/login", "Empty legacy endpoint did not receive the new default");
        });
        Check("automatic Wi-Fi requires an explicit saved network and strong mode requires Wi-Fi", delegate {
            PortalSettings s = Settings(); s.AutoConnectWifi = true;
            Assert(s.Validate() != null, "automatic Wi-Fi accepted no target");
            s.WifiProfile = "Fixture Campus"; Assert(s.Validate() == null, "configured Wi-Fi target rejected");
            s.AutoConnectWifi = false; s.WifiProfile = ""; Assert(s.Validate() == null, "authentication-only mode rejected");
            s.ForceConnect = true; Assert(s.Validate() != null, "strong Wi-Fi mode enabled without Wi-Fi control");
        });
        Check("legacy advanced options are replaced by the internal authentication preset", delegate {
            string path = Path.Combine(dataDirectory, "legacy-settings.xml");
            File.WriteAllText(path, "<PortalSettings><LoginUrl>https://portal.example.invalid/api/portal/v1/login</LoginUrl>"
                + "<WifiProfile>Fixture Campus</WifiProfile><ForceConnect>true</ForceConnect><CheckIntervalSeconds>30</CheckIntervalSeconds>"
                + "<RequestEncoding>Form</RequestEncoding><UsernameField>old-user</UsernameField><ExtraFields>tenant=old</ExtraFields>"
                + "<PortalUrl>https://old.example.invalid/</PortalUrl><SuccessField>old</SuccessField></PortalSettings>");
            PortalSettings s = PortalSettings.Load(path);
            Assert(s.AutoConnectWifi && s.WifiProfile == "Fixture Campus", "legacy Wi-Fi intent not migrated");
            Assert(s.RequestEncoding == "Json" && s.UsernameField == "username" && s.ExtraFields == "domain=default"
                && s.SuccessField == "reply_code" && s.SuccessValue == "0", "old advanced settings remained active");
            Assert(s.PortalUrl == "https://portal.example.invalid/portal/index.html", "old portal URL retained");
            PortalSettings.Save(path, s);
            string xml = File.ReadAllText(path);
            Assert(!xml.Contains("<RequestEncoding>") && !xml.Contains("<SuccessField>") && !xml.Contains("<ExtraFields>"),
                "advanced authentication settings remain user configuration");
        });
        Check("an explicit authentication-only choice survives save and reload", delegate {
            string path = Path.Combine(dataDirectory, "auth-only-settings.xml");
            PortalSettings s = Settings(); s.AutoConnectWifi = false; s.WifiProfile = "Previously Selected";
            PortalSettings.Save(path, s);
            Assert(!PortalSettings.Load(path).AutoConnectWifi, "stored Wi-Fi name silently re-enabled Wi-Fi control");
        });
        Check("account completeness requires one username and password pair", delegate {
            CredentialAccount c = new CredentialAccount(); Assert(!c.IsComplete, "Empty account accepted");
            c.Username = "dummy-user"; Assert(!c.IsComplete, "Missing password accepted");
            c.Password = "dummy-pass"; Assert(c.IsComplete, "Complete account rejected");
            c.Username = " "; Assert(!c.IsComplete, "Whitespace username accepted");
        });
        Check("credential editor preserves passwords only for unchanged users", delegate {
            CredentialAccount c = CredentialEditor.Build(Credentials(), "dummy-user", "");
            Assert(c.Password == "dummy-pass", "Unchanged credential lost");
            Throws(delegate { CredentialEditor.Build(Credentials(), "new-user", ""); });
        });
        Check("credential editor returns an independent account", delegate {
            CredentialAccount existing = Credentials();
            CredentialAccount c = CredentialEditor.Build(existing, "dummy-user", "");
            c.Password = "changed"; Assert(existing.Password == "dummy-pass", "Editor reused account instance");
        });
        Check("credential editor retains endpoint binding only for unchanged username", delegate {
            Assert(new CredentialAccount().BoundLoginUrl == "", "New account unexpectedly bound");
            CredentialAccount existing = Credentials(); existing.BoundLoginUrl = "https://portal.example.invalid/api/login";
            Assert(CredentialEditor.Build(existing, "dummy-user", "").BoundLoginUrl == "https://portal.example.invalid/api/login", "Unchanged username lost endpoint binding");
            Assert(CredentialEditor.Build(existing, "dummy-user", "new-pass").BoundLoginUrl == "https://portal.example.invalid/api/login", "Password edit lost endpoint binding");
            Assert(CredentialEditor.Build(existing, "other-user", "new-pass").BoundLoginUrl == "", "Changed username retained old endpoint binding");
        });
        Check("credential editor requires a complete account", delegate {
            Throws(delegate { CredentialEditor.Build(null, "", ""); });
            Throws(delegate { CredentialEditor.Build(null, "dummy-user", ""); });
            Throws(delegate { CredentialEditor.Build(null, "", "orphan"); });
            CredentialAccount c = CredentialEditor.Build(null, "dummy-user", "pass"); Assert(c.IsComplete, "Complete credential rejected");
        });
        Check("DPAPI credential store roundtrips and encrypts account", delegate {
            string path = Path.Combine(dataDirectory, "credentials.dat");
            CredentialAccount original = Credentials(); original.BoundLoginUrl = "https://portal.example.invalid/api/login";
            CredentialStore.Save(path, original); CredentialAccount c = CredentialStore.Load(path);
            Assert(c.IsComplete && c.Username == "dummy-user" && c.Password == "dummy-pass" && c.BoundLoginUrl == "https://portal.example.invalid/api/login", "DPAPI roundtrip failed");
            byte[] bytes = File.ReadAllBytes(path); string text = Encoding.UTF8.GetString(bytes);
            Assert(!text.Contains("dummy-user") && !text.Contains("dummy-pass") && !text.Contains("<CredentialAccount"), "Credential file contains plaintext");
        });
        Check("missing and corrupt credentials return independent empty accounts", delegate {
            string path = Path.Combine(dataDirectory, "missing-credentials.dat");
            CredentialAccount a = CredentialStore.Load(path); a.Username = "dummy-user"; a.Password = "dummy-pass";
            Assert(!CredentialStore.Load(path).IsComplete, "Missing credential accounts shared");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("dummy-invalid-ciphertext"));
            CredentialAccount c = CredentialStore.Load(path); Assert(!c.IsComplete, "Corrupt ciphertext accepted");
        });
        Check("credential store overwrites account password", delegate {
            string path = Path.Combine(dataDirectory, "overwrite-credentials.dat"); CredentialStore.Save(path, Credentials());
            CredentialStore.Save(path, CredentialEditor.Build(Credentials(), "dummy-user", "new-pass"));
            CredentialAccount c = CredentialStore.Load(path); Assert(c.Password == "new-pass", "Overwritten store retained old password");
        });
        Check("JSON payload correctly escapes credentials and extra values", delegate {
            PortalSettings s = Settings(); s.ExtraFields = "tenant=demo\nreturn=a=b";
            CredentialAccount c = new CredentialAccount { Username = "dummy\"\\\n雪", Password = "dummy&=+ pass" };
            Dictionary<string, object> payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(PortalProtocol.BuildPayload(s, c));
            Assert((string)payload["username"] == c.Username && (string)payload["password"] == c.Password && (string)payload["tenant"] == "demo" && (string)payload["return"] == "a=b", "JSON payload changed values");
            Assert(PortalProtocol.ContentType(s) == "application/json; charset=utf-8", "JSON content type incorrect");
        });
        Check("form payload encodes names and values", delegate {
            PortalSettings s = Settings(); s.RequestEncoding = "Form"; s.UsernameField = "user name"; s.PasswordField = "pass&field"; s.ExtraFields = "tenant=demo+one";
            string body = PortalProtocol.BuildPayload(s, new CredentialAccount { Username = "dummy+雪", Password = "a&b=c pass" });
            var fields = HttpUtility.ParseQueryString(body);
            Assert(fields["user name"] == "dummy+雪" && fields["pass&field"] == "a&b=c pass" && fields["tenant"] == "demo+one", "Form encoding corrupted values");
            Assert(PortalProtocol.ContentType(s) == "application/x-www-form-urlencoded; charset=utf-8", "Form content type incorrect");
        });
        Check("payload builder rejects credential override and incomplete accounts", delegate {
            PortalSettings s = Settings(); s.ExtraFields = "username=override";
            Throws(delegate { PortalProtocol.BuildPayload(s, Credentials()); });
            Throws(delegate { PortalProtocol.BuildPayload(Settings(), new CredentialAccount { Username = "dummy" }); });
        });
        Check("success accepts actual top-level scalar values", delegate {
            PortalSettings s = Settings(); Assert(PortalProtocol.IsSuccess(s, "{\"reply_code\":0}"), "Numeric success rejected");
            Assert(PortalProtocol.IsSuccess(s, "{\"reply_code\":\"0\"}"), "String success rejected");
            s.SuccessField = "ok"; s.SuccessValue = "true"; Assert(PortalProtocol.IsSuccess(s, "{\"ok\":true}"), "Boolean success rejected");
            s.SuccessValue = "false"; Assert(PortalProtocol.IsSuccess(s, "{\"ok\":false}"), "False boolean success rejected");
        });
        Check("success resolves dotted paths with invariant numeric formatting", delegate {
            PortalSettings s = Settings(); s.SuccessField = "data.result.code"; s.SuccessValue = "1.5";
            CultureInfo prior = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert(PortalProtocol.IsSuccess(s, "{\"data\":{\"result\":{\"code\":1.5}}}"), "Nested invariant number rejected"); }
            finally { CultureInfo.CurrentCulture = prior; }
        });
        Check("success rejects malformed unrelated null and composite JSON", delegate {
            PortalSettings s = Settings(); string[] responses = { null, "", "not json reply_code:0", "{bad}", "{\"reply_code\":1,\"message\":\"reply_code:0\"}", "{\"other\":{\"reply_code\":0}}", "{\"reply_code\":null}", "{\"reply_code\":[]}", "{\"reply_code\":{}}", "[{\"reply_code\":0}]", "{\"reply_code\":0}garbage" };
            foreach (string response in responses) Assert(!PortalProtocol.IsSuccess(s, response), "Non-success response accepted");
            s.SuccessField = "missing.code"; Assert(!PortalProtocol.IsSuccess(s, "{\"reply_code\":0}"), "Missing dotted property accepted");
        });
        Check("response classification distinguishes rejection from unknown data", delegate {
            PortalSettings s = Settings(); bool success;
            Assert(PortalProtocol.TryReadSuccess(s, "{\"reply_code\":0}", out success) && success, "Success not classified");
            Assert(PortalProtocol.TryReadSuccess(s, "{\"reply_code\":1}", out success) && !success, "Explicit rejection treated as unknown");
            string[] unknown = { null, "{bad}", "{}", "{\"reply_code\":null}", "{\"reply_code\":[]}", "{\"reply_code\":{}}", "{\"reply_code\":0} trailing" };
            foreach (string response in unknown) Assert(!PortalProtocol.TryReadSuccess(s, response, out success) && !success, "Unknown response authorized another submission");
        });
        Check("response parser accepts strict JSON only and rejects ambiguous duplicate keys", delegate {
            PortalSettings s = Settings(); bool success;
            string[] responses = { "{reply_code:0}", "{'reply_code':0}", "{\"reply_code\":0,}", "{\"reply_code\":+0}", "{\"reply_code\":00}", "{\"reply_code\":0,\"reply_code\":1}", "{\"reply_code\":1,\"reply_code\":0}", "{\"reply_code\":0,\"reply_\\u0063ode\":1}" };
            foreach (string response in responses) Assert(!PortalProtocol.TryReadSuccess(s, response, out success) && !success, "Non-JSON or ambiguous response accepted");
        });
        Console.WriteLine("Core test result: " + (failures == 0 ? "PASS" : "FAIL") + " (" + failures + " failed)");
        return failures == 0 ? 0 : 1;
    }
}
