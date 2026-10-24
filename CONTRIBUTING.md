# 参与贡献

欢迎提交问题、文档改进和小范围修复。请先搜索已有 issue；较大的功能或新校园认证协议，建议先通过功能建议说明具体场景和期望行为。交流请遵守[行为准则](CODE_OF_CONDUCT.md)。

## 反馈问题

使用仓库的问题反馈表单，提供应用版本、Windows 版本、复现步骤和预期/实际结果。只附与问题有关、经过人工去敏的少量日志或截图。

请勿提交账号、密码、令牌、本机代理的具体地址/IP、真实认证响应、未经清理的配置或完整诊断包。不要上传 `settings.xml`、`credentials.dat`、代理备份、自启任务导出或 Wi-Fi 配置。示例请使用虚构值，例如 `https://portal.example.invalid/api/login`。安全问题按[安全报告指引](SECURITY.md)处理。

无需为提交 issue 或 PR 再次进行真实认证、切换 Wi-Fi 或修改系统代理。是否在真实校园网复现由你自行决定；描述已有观察即可。

## 开发与提交

项目面向 Windows x64、.NET Framework 4.8、C# 5 和 Windows PowerShell 5.1，使用系统自带的 C# 编译器与框架类库，没有 `.csproj` 或 NuGet 恢复步骤。源码位于 `src/`。目录、测试约束及行为兼容要求见[开发指南](docs/DEVELOPMENT.md)。

从仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

提交涉及发布内容的改动时，再运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Package.ps1
```

`Package.ps1` 默认会重新测试和构建。测试当前包含五套入口、138 个用例，使用离线假环境、隔离数据目录和实际窗体的离屏检查；少量检查会读取系统状态，不代表校园网实测。测试和 CI 不得发送真实认证请求、主动无线扫描、修改 Wi-Fi、系统代理或自启任务。

PR 应解释具体问题、最终行为、验证命令与结果，以及尚未验证的场景。改动尽量集中；修复行为时增加有意义的离线回归覆盖。不要以“测试通过”代替对真实校园网兼容范围的说明。

新增可分发文档、脚本或素材时，需主动检查 `Package.ps1` 的文件清单，禁止递归打包整个项目。发布核验见[发布指南](docs/RELEASING.md)。贡献的代码和素材须有权按项目 MIT 许可提供；保留现有署名，并披露第三方来源及许可。
