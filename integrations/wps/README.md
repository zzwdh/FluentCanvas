# Fluent Canvas 的 WPS 演示加载项

此加载项供支持 JavaScript 加载项的 Windows 桌面版 WPS 演示试用。安装后，Fluent Canvas 可以跟随正在进行的放映并控制上一页、下一页和结束放映。墨迹会随放映页面切换并在当次放映中保留。自动截图、跨次恢复批注、WPS 内置缩略导航仍需对应的 WPS 能力和实机验证。

## 在测试机器加载

1. 准备支持演示加载项的 WPS 桌面安装包和官方 `wpsjs` 工具。运行 `wpsjs create FluentCanvasWps`，选择「演示」和「无」前端框架。
2. 用本目录的 `main.js` 和 `ribbon.xml` 替换生成的项目中对应的文件。若生成器带有入口 HTML，保留它对 `main.js` 的引用。按生成项目的说明运行 `wpsjs debug`，确认 WPS 演示读取了该加载项。
3. 在 Fluent Canvas 设置中启用 WPS 放映。用本地 `.pptx` 和 `.dps` 开始放映，分别使用 WPS 操作和 Fluent Canvas 工具栏翻页。关闭并重开 WPS，测试先开始放映再启用 Fluent Canvas 的顺序。

正式部署用官方的 `wpsjs publish` 方式制作和分发加载项，按 WPS 文档托管生成文件，并由用户从 `publish.html` 启用。WPS 版本、组织策略和部署方式会影响可用性。当前仓库提供加载项源码，尚未发布安装地址。测试步骤以 WPS 开放平台的开发说明为准。 [创建加载项](https://open.wps.cn/documents/app-integration-dev/wps365/client/wpsoffice/wps-integration-mode/wps-addin-development/generate-the-first-wps-addin) · [部署加载项](https://open.wps.cn/documents/app-integration-dev/wps365/client/wpsoffice/wps-integration-mode/wps-addin-development/wps-addin-development-instructions)

## 本地通信和验收

加载项使用 WPS 桌面演示对象模型读取放映状态，并在当前用户的 `%APPDATA%\Fluent Canvas WPS Bridge\v1` 建立专用命令文件。Fluent Canvas 读取当前页和放映会话标识，并向该现有文件写入限时翻页命令。通信不包含文件名、文稿路径、内容或墨迹，也不需要开放端口。每个加载项实例只有一个命令槽，Fluent Canvas 等待 WPS 确认后接受下一条。WPS 与 PowerPoint 的设置相互独立。 [WPS 放映视图](https://open.wps.cn/documents/app-integration-dev/wps365/client/wpsoffice/jsapi/wpp/SlideShowView/obj) · [WPS 演示事件](https://open.wps.cn/documents/app-integration-dev/wps365/client/wpsoffice/jsapi/addin-api/event/wpp-event) · [WPS 文件接口](https://open.wps.cn/documents/app-integration-dev/wps365/client/wpsoffice/jsapi/addin-api/FileSystem/obj)

同时出现多个 WPS 放映时暂停 WPS 联动，直到只剩一个可识别的放映。PowerPoint 与 WPS 同时放映时，Fluent Canvas 保持已选中的放映，直到放映结束或关闭对应的连接。加载项停止响应后，Fluent Canvas 会丢弃过期的放映状态。关闭 WPS 联动会取消尚未发出的命令，已经发出的操作可能由 WPS 完成。升级后需重新启用 WPS 联动。

运行 `node --test tests/wps-addin.test.cjs` 可验证加载项逻辑。Windows 侧的文件协议由文档回归测试覆盖。发布前在有实际 WPS 的设备上测试 WPS 与 PowerPoint 同时放映、窗口与全屏、外部翻页、结束或突然退出、中文路径，以及从 MSIX 和独立应用运行。验证 MSIX 能读取并更新加载项建立的命令文件。 [MSIX AppData 行为](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes#appdata-operations-on-windows-10-version-1903-and-later)
