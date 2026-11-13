# SoundPipe 桌面端 UI —— 设计系统落地说明

> 上游：`MASTER.md`（由 ui-ux-pro-max 生成：Glassmorphism / 深色科技 + 状态绿）
> 本文件记录 **WPF 桌面端的适配与偏离**，实现时以本文件 + MASTER.md 为准。

## 1. 设计系统来源

```
python <skill>/scripts/search.py "audio utility desktop control panel professional dark minimal" \
       --design-system -p "SoundPipe" --variance 4 --motion 3 --density 6
```

产出：模式 = Real-Time / Operations；风格 = Glassmorphism；配色 = 深色科技 + 状态绿；
间距 = 4/8 节奏；圆角 = 8（控件）/12（卡片）；反模式清单见 MASTER.md。

## 2. 布局（Operations Console 两栏工作台）

```
┌─ 顶部：品牌 + 运行状态胶囊（色点 + 文字） ────────────────────┐
├─ 左栏（配置） ───────────┬─ 右栏（监控） ─────────────────┤
│ 引导卡（缺虚拟声卡时）    │ 实时电平（三路 + dB 读数）      │
│ 转发路由（模式/目标/监听）│ 发声应用（勾选转发 + 状态点）    │
│ 转发参数（延迟/增益）     │ 开机自启                        │
│ 麦克风混音（渐进披露）    │                                 │
├─ 操作栏：主 CTA + 次操作 ─────────────────────────────────┤
├─ 状态栏：详细反馈 + 日志位置 ─────────────────────────────┤
```

- 主体分栏随窗口宽度按 `1.06* / 0.94*` 分配，两栏各自滚动（**底部操作栏与状态栏始终可见**，不被内容顶出屏幕）。
- 窗口尺寸上限绑定 `SystemParameters.WorkArea`，避免在高 DPI 小工作区下超出屏幕。

## 3. 相对 Web/移动版设计系统的适配（重要）

| MASTER.md 的规则 | WPF 落地方式 | 原因 |
|---|---|---|
| Heading/Body = Plus Jakarta Sans（Google Fonts） | `Microsoft YaHei UI, Segoe UI` 系统字体 | 桌面离线应用不应打包 Web 字体；需完整中文覆盖与原生字形 |
| `prefers-reduced-motion` | 读 `SystemParameters.ClientAreaAnimation`：关闭则不合并 `Themes/Controls.Motion.xaml` | Windows 等价的系统开关 |
| 玻璃质感（backdrop blur 10–20px） | 系统 Mica（`DWMWA_SYSTEMBACKDROP_TYPE`）+ 半透明表面令牌 | 由合成器实现，无自绘模糊的性能代价；失败回落渐变背景 |
| `cursor: pointer` | `Cursor="Hand"` | WPF 等价 |
| 响应式断点 375/768/1024 | 工作区尺寸钳制 + 双栏按比例 + 分区滚动 | 桌面窗口非移动断点模型 |
| 图表/数据可视化规则 | 电平表：色带填充 + **等宽 dB 数值读数** + 状态文字 | 颜色不单独承载语义 |
| 图标 | 全部矢量 `Path`；应用状态用矢量圆点 + 文字（原 `●/○` 字形已移除） | 禁 emoji/字形图标 |
| 数字排版 | dB 读数与 PID 用 `Cascadia Mono`（等宽） | 防止刷新时列宽跳动 |

## 4. 无障碍实现（对应 MASTER 优先级 1–2）

- **对比度**：正文 `#F8FAFC` / 次要 `#94A3B8` 对卡片 `#1B2336` 分别约 15:1 / 6.2:1；主按钮 `#0F172A` 对 `#22C55E` 约 7.9:1 —— 均高于 AA 4.5:1。
- **键盘焦点**：所有可交互控件带 2px 焦点环（`FocusVisual` 样式），Tab 顺序与视觉顺序一致。
- **高对比度主题**：`SystemParameters.HighContrast` 为真时最后合并 `Themes/HighContrast.xaml`，语义色整体替换为 `SystemColors`。
- **可访问名称**：`AutomationProperties.Name` 覆盖下拉框、滑块、复选框、电平表；状态栏用 `LiveSetting=Polite`。
- **信息不靠颜色**：电平有数值、应用状态有文字、运行胶囊有文字。

## 5. 文件地图

| 文件 | 职责 |
|---|---|
| `SoundPipe.App/Themes/Tokens.xaml` | 颜色/表面/描边/文字/字体/间距/卡片样式（唯一语义色来源） |
| `SoundPipe.App/Themes/Controls.xaml` | 控件模板（按钮/分段选择/下拉/复选/滑块/电平条/滚动条/托盘菜单/提示），无动画 |
| `SoundPipe.App/Themes/Controls.Motion.xaml` | 同上键名的**动效覆盖层**（150ms 淡入淡出），系统开启动画时才合并 |
| `SoundPipe.App/Themes/HighContrast.xaml` | 高对比度下的语义色覆盖 |
| `SoundPipe.App/Services/WindowEffects.cs` | DWM 深色标题栏 + Mica 材质 + 圆角（含 `CompositionTarget.BackgroundColor` 透明化） |
| `SoundPipe.App/MainWindow.xaml(.cs)` | 两栏工作台布局与交互 |

**动效实现注意**：Storyboard 必须可冻结，动画里**不能**用 `DynamicResource`（会使模板封存抛 `InvalidOperationException`）。因此动效独立成字典、按同键覆盖来开关，而非用令牌改时长。

## 6. 验证

- `dotnet build`：0 警告 0 错误。
- 项目自带验收断言（`tools/uia-smoke.ps1` 的附着版）：初始态 / 运行态 / 停止态 / 优雅关闭 **全通过（SMOKE OK）**。
- 视觉核对：`preview/before.png`（改造前）、`preview/idle.png`（待机）、`preview/running.png`（转发中，含 Mica 材质）。
- Mica 生效判据：客户区像素采样为壁纸色调（如 `#161D2C`）而非纯黑。

## 7. 已知差距 / 后续

- **托盘右键菜单的 `Separator`** 仍是系统默认样式，深色下对比偏弱。
- **MASTER.md 的 Landing 版式章节**（Hero/CTA/社交证明）不适用于桌面工具，未采纳。
- 「按应用独立音量」（需求 F6）已落地：勾选应用后每行展开音量滑条（旁显百分比），
  与总音乐增益相乘后经 `AudioEngine.SetAppGain` 实时生效，并按进程名持久化到 settings.json。