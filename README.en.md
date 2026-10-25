<p align="center">
  <img src="docs/images/hero.png" alt="CampusRelink — keep your campus connection in the background" width="100%">
</p>

<h1 align="center">CampusRelink</h1>

<p align="center">Connect once. Get on with your day.<br>A lightweight campus network authentication and reconnection tool for Windows.</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4?style=flat-square" alt="Windows 10 / 11 x64">
  <img src="https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?style=flat-square" alt=".NET Framework 4.8">
  <img src="https://img.shields.io/badge/version-1.0.3-0F766E?style=flat-square" alt="Version 1.0.3">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-334155?style=flat-square" alt="MIT license"></a>
</p>

<p align="center">
  <a href="README.md">简体中文</a> · English
</p>

<p align="center">
  <a href="#quick-start">Get started</a> ·
  <a href="#startup-proxy">Startup proxy recovery</a> ·
  <a href="#compatibility">Compatibility &amp; privacy</a> ·
  <a href="#faq">FAQ</a> ·
  <a href="#development">Development</a> ·
  <a href="https://github.com/fcwll666/CampusRelink/releases">Releases</a> ·
  <a href="https://github.com/fcwll666/CampusRelink/issues">Report an issue</a>
</p>

CampusRelink uses one username and password to authenticate when offline and attempt reconnection after a connection drops. It stays in the system tray, can optionally connect to a saved Wi-Fi network, and can authenticate over your current wired or wireless connection.

**The default targets Nanjing University of Chinese Medicine.** You can edit the login URL, but the app includes only one Srun-style protocol preset. Changing the address does not establish compatibility with another school or every Srun deployment. See [connection and authentication configuration](docs/PORTAL-CONFIGURATION.md) (Chinese).

> The default login endpoint uses HTTP, so authentication is not encrypted in transit. If your school provides a compatible HTTPS endpoint, enter it on the main page.

## Built for everyday connections

| Feature | What it does |
| --- | --- |
| Authentication and reconnection | Authenticates with your saved account when offline and preserves an existing online connection. |
| Optional automatic Wi-Fi connection | Searches nearby SSIDs on the computer running the app and connects using a saved Windows profile; wired authentication is also supported. |
| Tray and sign-in startup | Closes to the tray. Background operation and sign-in startup begin only after you choose Save and enable. |
| Local credential protection | Encrypts saved passwords with Windows DPAPI for the current user, with no telemetry or account sync. |
| Startup proxy recovery | Handles an inactive local proxy left in Windows when a VPN or proxy client was not closed properly before shutdown. Disables manual proxy use and restores automatic detection only when startup checks allow it. |
| Light and dark themes | Choose a theme and check connection status and logs from the main window. |

<a id="startup-proxy"></a>
### Recovering a proxy left behind after shutdown

Some VPN or proxy clients enable the Windows manual proxy and turn off automatic detection. If the client was not closed properly before shutdown, Windows may still point to its local proxy port at the next boot while the client is not running and the port has no listener. Browsers and other applications using the system proxy may then fail to reach the campus portal or authentication endpoint.

The startup check is enabled by default. Recovery requires all of the following:

1. **An eligible startup session.** The connection check starts during sign-in startup within the first **five minutes after Windows boots**. It runs at most once per boot, for at most **one minute**.
2. **An inactive local manual proxy.** Every configured manual proxy endpoint is a loopback address, and repeated checks find no listener on any of its ports.
3. **Three failed portal transport probes.** The first three portal probes through the current system proxy all fail with transport errors such as connection failures or timeouts. An HTTP response or an uncertain result preserves the settings. These probes are separate from account login attempts.
4. **Successful rechecks and backup.** The configuration is unchanged, the ports remain inactive, and the original settings are backed up. The app then disables manual proxy use and restores automatic detection, preserving the proxy address and other settings for later use.

**Reconnection during normal use never changes your proxy.** Active ports, remote proxy servers or uncertain conditions also preserve the settings. This feature repairs Windows proxy state; the app's authentication and connectivity checks use direct connections. Invalid credentials or an incompatible authentication protocol require separate troubleshooting. See the [full conditions](docs/FAQ.md) (Chinese).

<a id="quick-start"></a>
## Get started

Requirements: **Windows 10 / 11 x64 · .NET Framework 4.8**. Use the `CampusRelink-1.0.3-win-x64.zip` release package. Download, checksum and upgrade guidance is in the [installation guide](docs/INSTALLATION.md) (Chinese).

1. **Extract and open.** Extract the complete package to a directory you intend to keep, then run `CampusRelink.exe`. The first launch only opens configuration; it does not authenticate or register startup.
2. **Enter your account and endpoint.** Supply the username and password provided by your school and check the login URL. The default is `http://net.njucm.edu.cn/api/portal/v1/login`. To find an endpoint yourself, follow the steps below.
3. **Choose your connection.** For automatic wireless connection, click **搜索 Wi-Fi** (Search Wi-Fi) and select a network with a saved Windows profile. For an unsaved network, connect once in Windows, then search again. Disable **自动连接 Wi-Fi** (Automatically connect Wi-Fi) for wired networking or a wireless connection you manage yourself.
4. **Save and enable.** Click **保存并启用** (Save and enable) to start background operation and register startup when the current user signs in to Windows. **测试登录** (Test login) saves the configuration and submits a real authentication request without enabling startup.

Closing the window sends it to the tray. **停止后台** (Stop background) also removes sign-in startup. **退出** (Exit) in the tray ends the current session and keeps sign-in startup enabled.

<a id="find-authentication-endpoint"></a>
### Finding the campus authentication endpoint

The endpoint is the URL that receives the login request when you click **Log in**. It usually differs from the page in your browser's address bar. For NJUCM, the portal is `http://net.njucm.edu.cn/portal/index.html` (possibly with network parameters), while the default authentication endpoint is `http://net.njucm.edu.cn/api/portal/v1/login`.

1. **Connect to the campus network and open its login page.** Enter your credentials, but do not submit yet. If already signed in, capture the request the next time authentication is needed.
2. **Open developer tools first.** Press **F12**, or right-click the page and select **Inspect**, then open **Network**. Keep recording enabled and optionally clear older entries. Enable **Preserve log** if login navigates to another page.
3. **Submit the login form.** Look for the request generated by the login action, often containing `login` or `auth` in its name or path. Try **Fetch/XHR** first; switch to **All** if no matching request appears.
4. **Copy the actual request URL.** Select the login request and open **Headers → General → Request URL**. Check its method and payload to distinguish it from the HTML page, static resources or an `OPTIONS` preflight request.
5. **Enter the URL and check compatibility.** Paste the complete URL into CampusRelink's **登录 URL** field. The current preset requires **POST JSON** with `username`, `password` and `domain: "default"`, and accepts a response containing `reply_code: 0`. A different payload, encryption scheme or response format requires code adaptation even if you have found the correct URL.

See [connection and authentication configuration](docs/PORTAL-CONFIGURATION.md) for more detail (Chinese). Inspect requests locally; redact credentials, cookies and tokens before sharing captures.

<a id="compatibility"></a>
## Compatibility, permissions and privacy

- **Authentication scope.** The app stores one account. Its preset sends POST JSON with fixed `username`, `password` and `domain: "default"` fields, and accepts JSON `reply_code: 0`. CAPTCHA, browser interaction, dynamic challenges and other protocols require code adaptation. [Protocol and connection details →](docs/PORTAL-CONFIGURATION.md)
- **Networking and permissions.** Authentication and connectivity checks use direct connections. The connectivity probe does not include credentials, and HTTPS login retains normal certificate validation. Strong connection mode may change the wireless connection and require administrator rights; start with normal mode.
- **Proxy repair boundaries.** The check starts during sign-in startup within the first five minutes after boot, runs at most once per boot and lasts at most one minute. It changes the proxy switch only after three consecutive portal transport failures through the system proxy, inactive local loopback ports, unchanged conditions on recheck and a successful backup. Normal reconnection preserves proxy settings. [Full conditions →](docs/FAQ.md)
- **Local data.** Settings remain in `%LOCALAPPDATA%\CampusFlowCommunity\`; DPAPI protects passwords for the current Windows user. There is no telemetry or account sync. Logs may contain network state and error details, so review them before sharing.
- **Validation and distribution.** Packages contain no real credentials, personal configuration or developer-host Wi-Fi lists. Automated tests use simulated environments and do not establish live campus compatibility. The EXE is unsigned, so Windows may display a source warning.

<a id="faq"></a>
## FAQ

<details>
<summary><strong>Why does a saved password show 15 asterisks?</strong></summary>

`***************` is a fixed placeholder, not your password or an indication of its length. With an unchanged username, keeping the placeholder or leaving the field empty reuses the saved password. Changing the username requires a new password.

</details>

<details>
<summary><strong>Why must I connect in Windows before selecting some Wi-Fi networks?</strong></summary>

Search lists SSIDs visible near the computer running the app. Automatic connection also needs a valid saved Windows profile, including any wireless password. The app does not store wireless passwords or create profiles for unknown networks. Connect in Windows, then search again in the app. Opening the UI reads cached network information and saved profiles; only clicking Search Wi-Fi starts a scan.

</details>

<details>
<summary><strong>Can I use it at another school?</strong></summary>

The default endpoint targets Nanjing University of Chinese Medicine. Editing the URL can work only when the endpoint matches the built-in request and response protocol; compatibility with all Srun deployments is not implied. Other protocols need explicit implementation and validation. Read [connection and authentication configuration](docs/PORTAL-CONFIGURATION.md) first.

</details>

<details>
<summary><strong>How do I upgrade, move or rename the executable?</strong></summary>

Exit the old app from its tray, open `CampusRelink.exe` from the new location, and choose Save and enable. Existing CampusFlow Community settings and credentials are retained, together with the internal data and startup identifiers. If an older highest-privilege scheduled task still points to the previous path, updating that task requires the existing administrator authorization. Cancelling leaves the old task intact; keep the old directory until the update succeeds. See [installation and upgrades](docs/INSTALLATION.md).

</details>

More troubleshooting is available in the [FAQ](docs/FAQ.md) (Chinese).

<a id="development"></a>
## Build from source

Use Windows x64, .NET Framework 4.8, Windows PowerShell 5.1 and the system C# compiler. A normal application build requires neither NuGet nor Python.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Package.ps1
```

The executable is written to `artifacts\build\CampusRelink.exe`. By default, packaging runs tests and builds before creating source and Windows packages plus `SHA256SUMS`. The release version is **1.0.3**; the assembly version is **1.0.3.0**.

```text
src/          Application source
tests/        Isolated tests and offline UI checks
branding/     Original icons and generation script
docs/         Setup, compatibility, development and releases
.github/      Workflows and collaboration templates
```

[Development guide](docs/DEVELOPMENT.md) · [Release process](docs/RELEASING.md) · [Repository publication](docs/PUBLISHING.md) · [Changelog](CHANGELOG.md)

## Contribute and license

Reproducible issues and focused improvements are welcome. Use demo accounts, reserved example domains and fictional network names, and remove personal information before submitting. Read the [contribution guide](CONTRIBUTING.md) and [code of conduct](CODE_OF_CONDUCT.md); report security concerns through the [security policy](SECURITY.md).

This project uses the [MIT license](LICENSE). Read [NOTICE](NOTICE.md) for artwork provenance and related information.
