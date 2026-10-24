using System;

// The simple release uses the same authentication contract as the working
// original. NJUCM is the default; a different server URL remains user-editable.
internal static class BuiltInAuthentication
{
    internal const string DefaultLoginUrl = "http://net.njucm.edu.cn/api/portal/v1/login";
    internal static void Apply(PortalSettings settings)
    {
        settings.RequestEncoding = "Json";
        settings.UsernameField = "username";
        settings.PasswordField = "password";
        settings.ExtraFields = "domain=default";
        settings.SuccessField = "reply_code";
        settings.SuccessValue = "0";
        settings.ConnectivityUrl = "http://www.msftconnecttest.com/connecttest.txt";
        settings.ConnectivityExpected = "Microsoft Connect Test";
        settings.PortalUrl = ResolvePortalPage(settings.LoginUrl);
    }

    static string ResolvePortalPage(string loginUrl)
    {
        Uri login;
        if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out login)
            || (login.Scheme != Uri.UriSchemeHttp && login.Scheme != Uri.UriSchemeHttps)
            || !String.IsNullOrEmpty(login.UserInfo)) return "";
        const string suffix = "/api/portal/v1/login";
        string path = login.AbsolutePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? login.AbsolutePath.Substring(0, login.AbsolutePath.Length - suffix.Length) + "/portal/index.html" : "/";
        return new Uri(login.GetLeftPart(UriPartial.Authority) + path).AbsoluteUri;
    }
}
