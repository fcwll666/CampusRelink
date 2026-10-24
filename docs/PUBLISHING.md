# 首次上传 GitHub

此文档供项目维护者使用。[返回首页](../README.md)

## 新建空仓库

建议仓库名为 `CampusRelink`，可见性选择 **Public**。简介可填写：

> 校园网自动认证与断线重连工具，适用于 Windows，默认适配南京中医药大学。

不选择仓库模板，不额外生成 README、`.gitignore` 或许可证；这些文件已包含在目录中。GitHub 官方也建议导入已有项目时不要预填这些文件，以避免合并冲突：[创建仓库](https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository)。

## 上传哪些内容

将此项目根目录中的内容放到远程仓库根目录。打开仓库后应直接看到 `README.md`、`src/`、`docs/` 和 `.github/`，不要再套一层项目文件夹。

源码仓库包含代码、测试、构建脚本、文档、品牌素材和社区协作模板。程序 EXE、可下载 ZIP 与 `SHA256SUMS` 放到 **Releases**，不提交到源码根目录。

`artifacts/`、`dist/`、用户配置、凭据、代理备份和运行日志不属于公开源码。`.gitignore` 已排除常见本机产物；提交前仍应检查差异与图片内容。不要把旧个人程序目录或整个工作盘上传。

## 使用 Git 上传

以下命令在项目根目录运行，远程仓库为 [fcwll666/CampusRelink](https://github.com/fcwll666/CampusRelink)。

```powershell
git init -b main
git add .
git diff --cached --stat
git commit -m "Prepare CampusRelink open-source repository"
git remote add origin https://github.com/fcwll666/CampusRelink.git
git push -u origin main
```

如果此目录已经有 Git 历史，沿用现有仓库和远程配置，不要重复初始化。Windows GitHub Desktop 也可选择本地项目文件夹后推送到对应仓库。

首页已接入本仓库的 Releases、Issues 和 Actions。工作流首次成功后，可按实际情况添加动态构建状态徽章；静态测试数量不等于线上 CI 状态。

## 配置仓库展示

在仓库右侧 **About** 中设置简介与主题，例如 `windows`、`campus-network`、`auto-login`、`reconnect`、`wifi`、`njucm`。使用 `docs/images/hero.png` 作为分享预览图的素材时，按 GitHub 页面提示调整尺寸。

Issues 与 Actions 保持启用；按需开启 Security 中的 private vulnerability reporting。安全报告方式见 [SECURITY.md](../SECURITY.md)。无需添加尚未实际使用的赞助、官网、下载次数或覆盖率徽章。

## 发布可下载软件

先阅读[发布说明](RELEASING.md)。当前工作流对推送与 Pull Request 执行隔离测试和打包；推送 `v1.0.3` 标签时生成 **草稿 Release**。维护者检查文件与说明后，再在 GitHub 页面公开发布。

应用未签名，发布说明中保留运行环境、学校适配范围和已知限制。当前提供的是离线自动化验证结果，不能把它改写为所有校园网真实环境测试通过。

项目已包含 MIT 许可证及 [NOTICE.md](../NOTICE.md) 的来源说明。发布时保留项目署名与参考项目鸣谢，新增第三方材料时按其许可保留来源和必要说明。
