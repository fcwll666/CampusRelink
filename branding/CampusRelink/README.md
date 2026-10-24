# CampusRelink

**Campus network auto-reconnect · 校园网自动重连**

Campus 延续 CampusFlow 的校园场景；Relink 表示重新建立连接。正式名称统一写作 **CampusRelink**，GitHub 仓库名可采用 `campus-relink`。

图标以回环箭头组成字母 C 的轮廓，围绕 Wi-Fi 信号，表达网络断开后恢复连接。深蓝色承载主体，青绿色强调重连，白色表示网络信号。功能说明采用 campus network，同时涵盖有线与无线校园网。

## 素材

| 文件 | 用途 |
| --- | --- |
| CampusRelink-icon.svg | 应用图标矢量源文件，图形不依赖字体 |
| CampusRelink-icon.png | 1024 × 1024，外部留白透明 |
| CampusRelink.ico | Windows 图标，包含 16、20、24、32、40、48、64、96、128、256 像素 |
| CampusRelink-symbol.svg | 单色符号，通过 CSS `color` 设置颜色 |
| CampusRelink-tray-white.png | 深色背景用白色透明符号 |
| CampusRelink-tray-dark.png | 浅色背景用深蓝透明符号 |
| CampusRelink-wordmark.svg / .png | 名称与图标的横版组合，PNG 背景透明 |
| CampusRelink-preview.png | 整体方案及小尺寸预览 |

配色：深蓝 `#12324B`，青绿 `#38D9B5`，信号白 `#F7FCFF`。ICO 各尺寸采用带透明度和 AND 遮罩的 32 位 DIB 编码，兼容 Windows 和 .NET Framework 的图标显示。字标使用 Windows 自带的 Segoe UI，中文说明使用微软雅黑；不包含或重新分发字体文件。字标 SVG 含文本，显示时使用系统字体及回退字体。

自 1.0.3 起，新名称与图标已集成到程序、窗口、托盘、关于页面和发布包。图标嵌入可执行文件，不依赖外部素材目录。为兼容升级，原有配置目录、开机启动注册名及后台信号标识保持不变。名称未进行商标或仓库可用性检索。

重建素材：在 Windows 中安装 Pillow 后运行 `python build_brand_assets.py`；无需启动校园网程序，不执行联网、代理或 Wi-Fi 操作。新增素材及构建脚本沿用项目 MIT 许可。
