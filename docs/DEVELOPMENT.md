# 开发指南

## 工具链与目录

开发环境为 Windows x64、.NET Framework 4.8 和 Windows PowerShell 5.1。构建脚本使用 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，明确启用 C# 5 与 x64 目标，并检查已安装的框架版本。只使用框架类库，没有 `.csproj`、NuGet 恢复或 `dotnet build` 步骤。

| 路径 | 内容 |
| --- | --- |
| `src/` | 应用源码，由构建助手收集 C# 文件 |
| `tests/` | 五套独立测试入口 |
| `scripts/BuildSupport.ps1` | 共享编译、图标嵌入和测试结果解析助手 |
| `scripts/Test-ResultParser.ps1` | 测试结果解析器自检 |
| `branding/CampusRelink/` | 可重建品牌素材、ICO 与生成脚本 |
| `docs/` | 使用、开发和发布文档 |
| `.github/workflows/build.yml` | Windows 构建、打包与草稿 Release 工作流 |
| `artifacts/`、`dist/` | 本机构建、测试报告、打包暂存和发布产物，均不提交 |

`.editorconfig` 为 C# / PowerShell 约定 UTF-8 BOM，确保 Windows PowerShell 5.1 正确读取中文；Git 行尾由 `.gitattributes` 约定。无需为了单个改动批量重写既有文件。

## 构建与离线测试

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建输出为 `artifacts\build\CampusRelink.exe`，当前程序集和文件版本均为 `1.0.3.0`。构建不会启动正常应用入口。

`Test.ps1` 当前包含 138 个用例，按以下入口逐套编译、执行：

| 测试入口 | 关注点 |
| --- | --- |
| `CommunityCoreTests` | 配置、凭据、认证数据与通用逻辑 |
| `ConnectionFlowTests` | 连接结果、自启处理及失败分支 |
| `StartupProxyTests` | 开机代理修复条件与保留原设置的边界 |
| `UiSmokeTests` | 实际窗体构造、状态和离屏渲染 |
| `WifiProfilesTests` | 假 Wi-Fi 搜索源、已保存配置和选择行为 |

每个测试子进程通过 `CAMPUSFLOW_COMMUNITY_DATA_DIR` 使用独立数据目录。报告位于 `artifacts\tests\<运行标识>\`，包含每套标准输出、错误输出，以及逐用例 `results.json` / `results.csv`。新增用例应输出明确的 `PASS case-name` 或 `FAIL case-name`，失败时返回非零退出码。

这些是离线假环境测试，少量用例会查询系统只读状态。它们不代表真实校园网验证。HTTP、Wi-Fi、自启写操作和代理写操作需通过假对象隔离；测试和 CI 不得发送真实认证请求、调用原生活跃无线扫描、修改 Wi-Fi、系统代理或计划任务。UI 检查只构造、检查并离屏渲染窗体，不创建托盘图标或启动后台连接。不要把正常应用启动、真实登录或网络设置修复加入 CI。

是否在自己的真实校园网络验证由开发者自行决定。此类结果须单独描述环境和范围，不能以自动化通过代替；提交 PR 不要求提供校园账号或真实网络访问。

## 保持兼容的行为

- 使用一组账号和一种内置 Srun 风格 POST JSON 认证预设。区分认证接受、明确拒绝、未知响应与传输失败；不增加隐式备用账号。改变接口地址不等于适配其他门户协议。
- 固定 15 个星号表示已保存密码，与实际密码长度无关。只有用户名不变时，未改动占位提示或留空才沿用已有密码；更改用户名需重新填写密码。凭据改动需覆盖保存、重读与用户名变化。
- 保留 `CampusFlowCommunity` 数据目录和 Run 名称、`CampusFlow_Community` 计划任务名称、既有互斥体及测试环境变量。品牌改名不迁移这些兼容标识。
- 打开界面只读取缓存和已保存 Wi-Fi 配置；明确点击“搜索 Wi-Fi”才异步发起真实扫描。自动连接需要有效的已保存配置，配置名可能不同于 SSID。保留有效选择；无有效选择时只可预选当前已连接的配置，不默认选择无关的第一项。禁用自动连接后保持当前网络。
- 不猜测无线密码、不创建开放配置、不携带开发机 SSID 列表。测试仅使用假搜索源。
- 开机代理修复遇到不确定、活动或发生变化的配置时保留原设置；修改相关逻辑需覆盖这些保留分支。门户预取与探测地址的本机推导不是通用门户发现机制。

## 素材与文档

`branding/CampusRelink/CampusRelink.ico` 同时作为 EXE 图标和 `CampusRelink.AppIcon` 清单资源嵌入，测试编译也嵌入相同资源。运行时不依赖外部素材目录。普通构建直接使用仓库中的 ICO；只有重新生成品牌素材才需要 Python 3 与 Pillow。新增素材必须注明来源和再分发许可，不分发系统字体文件。

README 保留品牌横幅 `docs/images/hero.png`，不展示软件页面截图。

重新生成横幅时，可运行以下可选工具，需要 Python 3 与 Pillow，不启动应用或访问网络设置。

```powershell
python .\scripts\Build-ReadmeHero.py
```

源码包采用 `Package.ps1` 的明确文件清单。新增需要分发的文档、素材或脚本后，检查该清单与压缩包实际内容；不要改为递归复制项目目录。发布操作见[发布指南](RELEASING.md)。
