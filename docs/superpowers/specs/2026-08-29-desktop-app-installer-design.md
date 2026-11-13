# SoundPipe 桌面应用 + 安装程序 设计文档

日期：2026-08-29
状态：已批准（用户确认"设计整体合理，方案可行"）

## 1. 目标

把 SoundPipe（CLI 引擎已完成，30/30 测试通过）打包为可分发的 Windows 桌面应用：

1. 重建 WPF 图形界面（此前 M3 已验收后删除，现为空壳脚手架）
2. 发布为自包含单文件 exe（目标机器无需 .NET 8）
3. Inno Setup 制作 Setup.exe：Program Files 安装、桌面快捷方式+图标、开始菜单、开机自启（可选勾选）、卸载入口

## 2. UI 重建范围（SoundPipe.App，WPF）

与原 M3 验收布局一致，并合入后续新增功能：

| 区块 | 控件 | 行为 |
|---|---|---|
| 转发模式 | RadioButton ×2 | 整系统声音 / 指定应用 |
| 转发目标 | ComboBox | 虚拟声卡输入端优先默认选中（名称含 VoiceMeeter/CABLE） |
| 监听输出 | ComboBox | (无) + 物理输出设备列表 |
| 缓冲延迟 | Slider 10-100ms | 停止时生效 |
| 增益 | Slider 0-3x | 运行中实时生效（SetGain） |
| 麦克风混音 | CheckBox + ComboBox + Slider | 开关即时 EnableMic/DisableMic；设备列表过滤虚拟端点；增益 0-3x 实时 |
| 电平表 | ProgressBar ×3 | 源声音 / 总输出 / 麦克风，DispatcherTimer 150ms 轮询引擎电平 |
| 发声应用 | ItemsControl(CheckBox) | SessionMonitor 1s 快照驱动；●发暗中/○静默；勾选=AddApp，取消=RemoveApp；仅按应用模式生效 |
| 操作 | 开始转发/停止 Button | 状态栏文本反馈 |
| 工具 | 一键配置 / 链路诊断 Button | AutoSetup 幂等执行；诊断结果对话框（含自动拉起 Voicemeeter） |
| 开机自启 | CheckBox | 写/删 HKCU\...\CurrentVersion\Run "SoundPipe"（与安装器写同一键，单一来源=注册表，UI 为唯一修改入口） |
| 托盘 | Hardcodet NotifyIcon | 点 X 隐藏到托盘（转发继续）、双击恢复、菜单"打开/退出"（退出=真正停引擎+Dispose） |
| 首次引导 | 引导面板 | 枚举不到虚拟声卡输入端时显示：安装步骤 + "打开下载页"按钮（Process.Start 浏览器，不用 WebView2） |

线程约定：引擎回调（会话快照事件）经 Dispatcher 切回 UI 线程；电平表用 UI 定时器主动拉取，避免事件风暴。

## 3. 打包管线

### 3.1 发布（SoundPipe.App.csproj）

- `PublishSingleFile=true`、`SelfContained=true`、`RuntimeIdentifier=win-x64`、`IncludeNativeLibrariesForSelfExtract=true`
- `ApplicationIcon=Assets/app.ico`
- 第三方依赖嵌入验证：NAudio（托管程序集，默认内嵌）；无原生包依赖（WebView2 引用随引导简化一并移除）；发布后用 `"工具依赖清单" + 启动冒烟` 验证无运行时缺失
- 已知体量：自包含 WPF 约 70-100MB（单文件含运行时），可接受

### 3.2 图标生成

- 脚本 `tools/make-icon.ps1`（System.Drawing）：蓝底圆角 + 白色扬声器+声波简洁图形
- 一次生成 16/32/48/256 四种尺寸，PNG 压缩帧拼装多尺寸 .ico（Vista+ 支持），保证各 DPI 清晰

### 3.3 安装程序（Inno Setup 6）

- `installer/installer.iss`：
  - 默认装到 `{autopf}\SoundPipe`；发布单文件 + LICENSE/README
  - 桌面快捷方式 + 开始菜单目录（卸载时清理）
  - `[Tasks]` 勾选项"开机自动运行 SoundPipe"→ `[Registry]` HKCU Run 键（默认不勾，尊重用户）
  - `[Run]` 安装完可选直接启动；卸载删 Run 键
  - VersionInfo 与 exe 图标一致
- `installer/build-installer.ps1`：dotnet publish → 调 ISCC.exe → 产出 `dist/SoundPipe-Setup-<version>.exe`
- ISCC 缺失时脚本给出 winget 安装提示（`winget install JRSoftware.InnoSetup`）

## 4. 验收标准（含用户补充）

1. `dotnet build` 0 警告 0 错误；30/30 单测通过
2. UI 冒烟：启动→一键配置→开始转发→电平表随播放跳动→停止→退出（UIAutomation 或手动脚本验证）
3. Setup.exe `/SILENT` 安装成功：Program Files 文件齐全、桌面图标存在、快捷方式启动正常
4. 自启键：勾选任务后 HKCU Run 键写入；UI 开关可增删同一键
5. **卸载残留检查**：卸载后以下全部清理——安装目录、开始菜单目录、桌面快捷方式、HKCU Run 键（保留用户数据的 logs 目录可选保留，默认删除）

## 5. 非目标

- 不做数字签名（个人使用，免费方案）
- 不做多语言、不做自动更新
- 不改动 SoundPipe.Core / Cli 功能与行为
