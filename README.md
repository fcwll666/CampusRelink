<p align="center">
  <img src="docs/images/hero.png" alt="CampusRelink — 让校园网连接回到日常" width="100%">
</p>

<h1 align="center">CampusRelink</h1>

<p align="center">自动认证，断线重连，安静驻留托盘。<br>适用于 Windows 的校园网连接工具。</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4?style=flat-square" alt="Windows 10 / 11 x64">
  <img src="https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?style=flat-square" alt=".NET Framework 4.8">
  <img src="https://img.shields.io/badge/version-1.0.3-0F766E?style=flat-square" alt="Version 1.0.3">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-334155?style=flat-square" alt="MIT license"></a>
</p>

<p align="center">
  简体中文 · <a href="README.en.md">English</a>
</p>

<p align="center">
  <a href="#quick-start">下载与使用</a> ·
  <a href="#startup-proxy">开机代理适配</a> ·
  <a href="#compatibility">适配与隐私</a> ·
  <a href="#faq">常见问题</a> ·
  <a href="#development">参与开发</a> ·
  <a href="https://github.com/fcwll666/CampusRelink/releases">Releases</a> ·
  <a href="https://github.com/fcwll666/CampusRelink/issues">反馈问题</a>
</p>

CampusRelink 使用一组账号与密码，在离线时完成校园网认证，并在连接中断后尝试恢复。它可以驻留托盘，按需自动连接已保存的 Wi-Fi，也可以只在当前有线或无线网络上认证。

**当前默认适配南京中医药大学。** 登录 URL 可编辑，内置协议仍只有一种 Srun 风格预设；更换地址不代表适配其他学校或所有 Srun 部署。详见 [连接与认证配置](docs/PORTAL-CONFIGURATION.md)。

> 默认登录接口使用 HTTP，认证内容在传输中未经加密。如学校提供兼容的 HTTPS 接口，可在主界面修改登录 URL。

## 功能一览

| 功能 | 使用体验 |
| --- | --- |
| 自动认证与重连 | 离线时使用已保存的账号认证，已经在线时保留当前连接。 |
| 可选 Wi-Fi 自动连接 | 点击「搜索 Wi-Fi」查询这台电脑附近的 SSID，选择 Windows 已保存的网络；也支持仅认证有线网络。 |
| 托盘与登录自启 | 关闭窗口后留在托盘；点击「保存并启用」后才启动后台并注册登录自启。 |
| 本机凭据保护 | 密码使用 Windows DPAPI 按当前用户加密保存，无遥测、无账号同步。 |
| 开机阶段代理适配 | 处理上次关机前未正确退出 VPN / 代理客户端而遗留的失效本机代理；满足开机检查条件后关闭手动代理并恢复自动检测。 |
| 明暗主题 | 按偏好切换浅色或深色界面，在主界面查看连接状态与运行日志。 |

<a id="startup-proxy"></a>
### 开机阶段代理适配：处理上次关机遗留的失效代理

部分 VPN / 代理客户端会开启 Windows 的「手动代理服务器」，并关闭「自动检测设置」。如果上次关机前没有正常退出客户端，下次开机时，Windows 可能仍指向原来的本机代理端口，而代理程序尚未启动、该端口也没有监听。浏览器等使用系统代理的程序就可能无法打开校园网认证页面或访问认证接口，导致登录失败。

CampusRelink 默认启用开机阶段代理检查，只有同时满足以下条件才尝试修复：

1. **处于本次开机的自启阶段。** 在 Windows 开机后的前 **5 分钟内开始本轮连接检查**，每次开机最多检查一次；本轮最多持续 **1 分钟**。
2. **确认遗留的是失效本机代理。** Windows 已开启手动代理，配置中的全部代理地址均为本机回环地址，且反复检查对应端口均无监听。
3. **前三次门户探测均连接失败。** 使用当前系统代理连续进行三次门户探测，均因连接、超时等传输错误失败；收到 HTTP 响应或结果无法确认时保留设置。这三次探测不等于三次账号登录失败。
4. **复查与备份成功。** 确认代理设置未变化、端口仍无监听，并成功保存原配置备份后，才关闭「手动代理服务器」、恢复「自动检测设置」。原代理地址及其他配置保留，方便之后继续使用代理客户端。

**正常使用期间的断线重连不会修改你的代理。** 如果代理端口已经在监听、手动代理指向远程服务器，或检查条件无法确认，也会保留原设置。此功能针对遗留的 Windows 手动代理；软件的认证请求与联网检测本身使用直连，账号错误或认证协议不兼容仍需单独排查。完整条件见 [代理修复说明](docs/FAQ.md)。

<a id="quick-start"></a>
## 下载与使用

运行环境：**Windows 10 / 11 x64 · .NET Framework 4.8**。发行文件将在本仓库的 [Releases](https://github.com/fcwll666/CampusRelink/releases) 提供，选择 `CampusRelink-1.0.3-win-x64.zip`；获取、校验与升级说明见 [安装指南](docs/INSTALLATION.md)。

1. **解压并打开。** 将发布包完整解压到准备长期保留的目录，运行 `CampusRelink.exe`。首次打开只显示配置界面，不会自动认证或注册自启。
2. **填写账号与接口。** 输入学校提供的用户名、密码，核对登录 URL。默认地址为 `http://net.njucm.edu.cn/api/portal/v1/login`；需要自行查找时，按下方步骤获取认证接口。
3. **选择连接方式。** 需要自动连接无线网络时，点击「搜索 Wi-Fi」并选择已有 Windows 配置的目标。未保存的网络需先在 Windows 中连接一次，再重新搜索。使用有线或已手动连接的无线网络时，可取消「自动连接 Wi-Fi」。
4. **保存并启用。** 确认选项后点击「保存并启用」，启动后台连接并注册当前用户登录 Windows 后自启。需要检查账号时可使用「测试登录」；它会保存配置并发送真实认证请求，不启用自启。

窗口关闭后收进托盘。**「停止后台」会同时移除登录自启；托盘「退出」结束本次运行，保留登录自启。**

<a id="find-authentication-endpoint"></a>
### 如何获取校园网认证接口

认证接口是浏览器在你点击「登录」时实际提交账号密码的请求地址。它通常与浏览器地址栏中的登录网页地址不同：例如南中医的登录页面为 `http://net.njucm.edu.cn/portal/index.html`（后面可能带有网络参数），本软件默认填写的认证接口则为 `http://net.njucm.edu.cn/api/portal/v1/login`。

1. **连接校园网络，打开学校认证页面。** 填写用户名和密码，先不要点击「登录」。如果已经登录，可在下一次需要认证时操作。
2. **先打开开发者工具。** 按 **F12**，或在页面空白处右键选择「检查」，切换到 **Network（网络）** 标签页。保持录制开启；如已有很多记录，可先清空。若登录会跳转页面，可勾选 **Preserve log（保留日志）**。
3. **再点击登录，记录实际请求。** 保持开发者工具打开，在认证页面点击「登录」。请求列表会出现本次登录产生的条目；可先查看 **Fetch/XHR**，找不到时切回 **All（全部）**，查找名称或路径含 `login`、`auth` 等字样的请求。
4. **查看请求地址。** 点击实际提交登录信息的请求，在右侧 **Headers（标头 / 请求标头）→ General（常规）→ Request URL（请求网址）** 中复制完整地址。不要误选 HTML 登录页面、图片、脚本或 `OPTIONS` 预检请求；请求名称只是线索，还应结合请求方法和请求正文确认。
5. **填写并确认协议。** 将地址填入 CampusRelink 的「登录 URL」。当前内置协议要求 **POST + JSON**，请求字段为 `username`、`password`、`domain: "default"`，以响应中的 `reply_code: 0` 判定认证接受。即使找到了 URL，其他学校的请求格式、加密方式或成功判定不同，也仍需修改代码适配。

更详细的说明见 [连接与认证配置](docs/PORTAL-CONFIGURATION.md)。在本机查看即可，分享请求截图或记录前请遮盖账号、密码、Cookie 和令牌。

<a id="compatibility"></a>
## 适配、权限与隐私

- **认证范围。** 仅保存一组账号密码。内置请求为 POST JSON，固定字段 `username`、`password`、`domain: "default"`，以 JSON `reply_code: 0` 判定接受认证。验证码、浏览器交互、动态挑战或其他协议需要修改代码适配。[查看协议与连接说明 →](docs/PORTAL-CONFIGURATION.md)
- **网络与权限。** 认证及联网检查使用直接连接；联网检查不携带账号密码，HTTPS 登录保留正常证书验证。强连接模式可能改变无线连接状态并需要管理员权限，通常先使用普通模式。
- **代理修复边界。** 仅在开机自启阶段、开机后前五分钟内开始检查，每次开机最多一次，单轮最多一分钟；前三次系统代理门户探测均为传输失败，且全部手动代理为本机回环地址、端口持续无监听，复查并备份成功后才修改开关。运行期间的断线重连保留代理设置。[查看完整条件 →](docs/FAQ.md)
- **本地数据。** 数据保存在 `%LOCALAPPDATA%\CampusFlowCommunity\`，密码由当前 Windows 用户的 DPAPI 保护。程序没有遥测或账号同步；日志可能包含网络状态和错误信息，分享前请检查。
- **使用与验证。** 发布包不包含真实账号、个人配置或开发机 Wi-Fi 列表。自动化测试使用模拟环境，不能代替校园实网验证。发布的 EXE 未签名，Windows 可能显示来源提示。

<a id="faq"></a>
## 常见问题

<details>
<summary><strong>保存后为什么显示 15 个星号？</strong></summary>

`***************` 是固定占位提示，不是真实密码，也不反映密码长度。用户名不变时，保留占位提示或将密码框留空会沿用已保存的密码；修改用户名时必须同时填写新密码。

</details>

<details>
<summary><strong>搜索到了 Wi-Fi，为什么仍需先在 Windows 连接？</strong></summary>

搜索结果来自运行本应用的电脑附近可见的 SSID。自动连接还需要有效的 Windows 已保存配置，包括所需的无线密码；应用不保存无线密码，也不会为未知网络自动创建配置。先在 Windows 连接目标网络，再返回应用重新搜索。打开界面只读取缓存与已保存配置，点击「搜索 Wi-Fi」才发起扫描。

</details>

<details>
<summary><strong>能用于其他学校吗？</strong></summary>

默认接口针对南京中医药大学。只有接口与内置请求、响应协议兼容时，修改登录 URL 才可能适用；不能由此推断兼容所有 Srun 部署。不同认证协议需要明确的代码适配与验证。请先阅读 [连接与认证配置](docs/PORTAL-CONFIGURATION.md)。

</details>

<details>
<summary><strong>从旧版升级，或移动、改名程序后怎么办？</strong></summary>

先从托盘退出旧程序，打开新位置的 `CampusRelink.exe` 后点击「保存并启用」。原 CampusFlow Community 的本机配置和凭据继续读取，内部数据目录与启动标识保留兼容。若旧版最高权限计划任务仍指向原路径，需要通过原有管理员授权更新该任务；取消授权时会保留原任务，确认更新成功后再整理旧目录。详见 [安装与升级](docs/INSTALLATION.md)。

</details>

更多排查方法见 [常见问题与故障排查](docs/FAQ.md)。

<a id="development"></a>
## 从源码构建

需要 Windows x64、.NET Framework 4.8、Windows PowerShell 5.1 与系统 C# 编译器；常规构建无需 NuGet 或 Python。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Package.ps1
```

构建输出为 `artifacts\build\CampusRelink.exe`。打包脚本默认先测试与构建，再生成源码包、Windows 发布包和 `SHA256SUMS`；版本为 **1.0.3**，程序集版本为 **1.0.3.0**。

```text
src/          应用源码
tests/        隔离测试与离线界面检查
branding/     原创图标与生成脚本
docs/         安装、适配、开发与发布说明
.github/      工作流与协作模板
```

[开发指南](docs/DEVELOPMENT.md) · [发布流程](docs/RELEASING.md) · [构建记录](https://github.com/fcwll666/CampusRelink/actions) · [建仓与公开发布](docs/PUBLISHING.md) · [更新记录](CHANGELOG.md)

## 参与与许可

欢迎提交可复现的问题和聚焦的改进。请使用演示账号、保留示例域名与虚构网络名，提交前移除个人信息。开始前请阅读 [贡献指南](CONTRIBUTING.md) 与 [行为准则](CODE_OF_CONDUCT.md)；安全问题请按 [安全说明](SECURITY.md) 报告。

本项目采用 [MIT 许可证](LICENSE)。素材来源与相关说明见 [NOTICE](NOTICE.md)。
