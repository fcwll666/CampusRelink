using System;

internal enum AuthenticationOutcome { Accepted, Rejected, Unreachable, Unknown }

internal interface IConnectionEnvironment
{
    bool HasInternet();
    bool ConnectWifi(string profile, bool force);
    AuthenticationOutcome Authenticate(PortalSettings settings, CredentialAccount account);
    void Delay(int milliseconds);
    void Log(string message);
}

// Only the environment performs I/O. Account order and result handling are tested offline.
internal static class ConnectionCoordinator
{
    internal static string Run(PortalSettings settings, CredentialAccount credential, IConnectionEnvironment environment)
    {
        string error = settings == null ? "请先配置认证接口" : settings.Validate();
        if (error != null) return "配置无效：" + error;
        if (credential == null || !credential.IsComplete) return "尚未保存完整的账号和密码";
        if (environment.HasInternet()) return "网络已经可以正常访问";

        environment.Log("开始连接检测。");
        if (settings.AutoConnectWifi && !environment.ConnectWifi(settings.WifiProfile, settings.ForceConnect))
        {
            environment.Log("无线连接未就绪。");
            return "无线连接未就绪，请检查保存的 Wi-Fi 配置";
        }
        if (environment.HasInternet()) return "网络已经可以正常访问";
        AuthenticationOutcome outcome = environment.Authenticate(settings, credential);
        if (outcome == AuthenticationOutcome.Accepted)
        {
            for (int poll = 0; poll < 12; poll++)
            {
                if (environment.HasInternet())
                {
                    environment.Log("认证成功，联网复检通过。");
                    return "认证成功";
                }
                environment.Delay(1000);
            }
            environment.Log("认证已接受，等待网络放行；本轮不重复提交账户。");
            return "认证已接受，但联网复检暂未通过";
        }
        if (outcome == AuthenticationOutcome.Unknown)
        {
            environment.Log("认证结果无法确认，请检查认证链接或门户与内置协议的兼容性。");
            return "认证结果无法确认；请检查认证链接或联系网络管理员";
        }
        environment.Log(outcome == AuthenticationOutcome.Rejected ? "认证被拒绝。" : "认证接口暂不可达。");
        return outcome == AuthenticationOutcome.Rejected ? "认证被拒绝，请检查账号密码" : "认证接口暂不可达";
    }
}
