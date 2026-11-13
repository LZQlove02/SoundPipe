# SoundPipe — 声音转发到麦克风

把电脑里播放的声音（整系统或单个应用）实时转发到虚拟声卡的录音端，让 QQ、微信、录音机、会议/直播软件把它当作**麦克风**使用。支持通话时混入真实人声（音乐 + 说话双路独立音量）。

```
QQ音乐/网页/任何声音 ──→ SoundPipe 捕获 ──→ 虚拟声卡输入端
                          （人声可叠加 ↑）        │ 内核桥
                                    虚拟声卡录音端 ←┘
                                          ↓
                          QQ / 微信 / OBS / 录音机（选它当麦克风）
```

## 功能

- **两种转发模式**
  - 整系统：转发电脑播放的所有声音
  - 指定应用：只转发某个进程（含子进程树）的声音，基于 WASAPI 进程循环捕获，无需注入；运行中可增删应用，不停流
- **麦克风混音**：真实麦克风与音乐叠加转发，人声/音乐增益独立调节
- **双输出**：转发目标 + 可选监听设备（边转发边自己听），双时钟独立缓冲
- **一键自动配置**：自动选中虚拟声卡输入端为转发目标、把系统默认麦克风指向对应录音端、默认输出被吸入虚拟设备时自动切回物理扬声器
- **链路诊断**：向目标播放测试音并在录音端采样，回路测试定位断点；Voicemeeter 桥断时自动拉起本机已装的 Voicemeeter
- **稳定性**
  - 捕获/输出流异常自动重启（1s × 3 次，超限弃源并通知，稳定运行 10s 后计数重置）
  - 设备热插拔/默认设备变化：事件驱动，自动重建输出
  - 时钟漂移修正：丢样/补样 + crossfade，无爆音
  - 混音按需产出：源未攒满整块时等待而非补零，杜绝“半个包 + 静音空洞”造成的发闷/沙哑
  - tanh 软限幅：多源叠加不削波
- **发声应用枚举**：实时列出正在发声的应用（PID/进程名/活动状态）

## 环境要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 2004 (build 19041) 及以上（进程循环捕获的系统要求） |
| .NET | .NET 8 SDK |
| 虚拟声卡 | VB-CABLE（推荐）或 Voicemeeter（含 VAIO），任装其一 |

> 推荐装 [VB-CABLE](https://vb-audio.com/Cable/)：纯内核桥，装完永久生效，无需运行任何宿主程序。若已装 Voicemeeter 也可直接用（需在其面板把虚拟输入条的 B1 路由点亮，或用本项目的链路诊断自动修复）。

## 构建与运行

```powershell
git clone https://github.com/LZQlove02/SoundPipe.git
cd SoundPipe
.\dotnet.ps1 build SoundPipe.sln            # 构建（0 警告 0 错误）
.\dotnet.ps1 run --project SoundPipe.Cli    # 不带参数显示帮助
```

> 本机 PATH 中的 `dotnet` 只有运行时，`dotnet.ps1` 会自动定位用户目录里的 .NET 8 SDK（也可用 `$env:DOTNET_EXE` 指定），下面命令同此。

## CLI 命令参考

| 命令 | 说明 |
|---|---|
| `list` | 枚举播放设备（`*` 标记虚拟声卡输入端），显示默认设备与音量 |
| `apps` | 列出正在发声的应用（●发声中 / ○静默，含 PID） |
| `setup` | 一键自动配置（默认麦克风指向录音端 + 默认输出保护），幂等 |
| `linkdiag [--index n]` | 转发链路端到端回路诊断（约 5 秒，会有一声测试音） |
| `run` | 开始转发，Ctrl+C 停止，运行中实时显示电平条 |

### run 选项

| 选项 | 说明 | 默认 |
|---|---|---|
| `--mode system\|apps` | 转发模式 | system |
| `--app <pid[,pid...]>` | apps 模式的目标进程 PID | — |
| `--index n` / `--device <id>` | 转发目标设备 | 自动选虚拟输入端 |
| `--monitor <n>` | 监听设备序号（双输出） | 关 |
| `--capture <n>` | system 模式指定监听的渲染设备 | 系统默认输出 |
| `--mic <n>` | 混入麦克风（-1 = 默认物理麦克风，自动排除虚拟端防回环） | 关 |
| `--mic-gain <x>` | 麦克风增益 | 1.0 |
| `--latency <ms>` | 输出缓冲 10–100 | 20 |
| `--gain <x>` | 音乐增益 | 1.0 |
| `--duration <sec>` | 到时自动停止（冒烟测试） | 不限 |

## 典型用法

### 场景一：微信/QQ 通话时让对方听到 QQ 音乐 + 你说话

```powershell
.\dotnet.ps1 run --project SoundPipe.Cli -- setup        # 1. 只需一次
.\dotnet.ps1 run --project SoundPipe.Cli -- apps         # 2. 播放歌曲后查 QQMusic 的 PID
.\dotnet.ps1 run --project SoundPipe.Cli -- run --mode apps --app 17484 --mic -1
```

通话软件的麦克风保持**「默认」**即可（setup 已指向虚拟录音端）。建议戴耳机防回声。

### 场景二：录音机录制电脑播放的声音

```powershell
.\dotnet.ps1 run --project SoundPipe.Cli -- setup
.\dotnet.ps1 run --project SoundPipe.Cli -- run --mode system
```

录音软件选默认麦克风（或手动选 `CABLE Output` / `Voicemeeter Output`）开始录音。

### 场景三：只转发网页视频的声音

```powershell
.\dotnet.ps1 run --project SoundPipe.Cli -- apps          # 查 msedge/chrome 的 PID
.\dotnet.ps1 run --project SoundPipe.Cli -- run --mode apps --app 12345,12350
```

## 工作原理

### 音频链路

```
捕获源（三选一，均为轮询式 WASAPI，自研实现）
  ├─ SystemLoopbackCapture   整系统循环捕获（默认输出或指定渲染设备）
  ├─ ProcessLoopbackCapture  进程循环捕获（ActivateAudioInterfaceAsync + VAD\Process_Loopback）
  └─ MicrophoneCapture       麦克风（自动排除虚拟端点，防捕获到转发自身回环）
        ↓ SampleConverter（解码 float32/PCM16/32 → 降混/单声道展开 → WDL 重采样）
  引擎统一格式 48kHz / 2ch / float32
        ↓ SpScRingBuffer（SPSC 无锁环形缓冲，音频路径零分配）
  Mixer（多源增益叠加 → tanh 软限幅 → 多输出环）
        ↓ SpScRingBuffer ×2
  OutputWriter ×2（WASAPI 共享模式事件驱动）
        ├─ DriftCorrectedReader 深度自适应丢/补样（crossfade）
        └─ 按设备 MixFormat 转换（采样率/通道映射/PCM16）
        ↓
  转发目标（虚拟声卡输入端） + 可选监听设备
```

### 关键技术点

- **混音不切碎**：捕获侧是“每 10ms 突发送一包”，混音线程产能约 5× 实时。若饿了就取，会在半个包上补零 → 音频流被静音空洞切碎，听感发闷/沙哑，而且这种欠载不报错、极难察觉。`Mixer.TryProcess` 只在活跃源攒满整块时产出，8ms 宽限后仍不足才强制补零并计数（日志每 10s 一行「混音诊断」，补零应为 0）
- **延迟构成**：混音环 2 个量化块（20ms）+ 输出缓冲（默认 20ms，可调至 10ms）+ 一个捕获周期（10ms）。输出周期同为 10ms，所以环再加深只会增加延迟、不提升稳定性
- **进程循环捕获**：`mmdevapi!ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`，虚拟设备路径 `VAD\Process_Loopback`。激活参数 blob 的释放在完成回调触发后执行（提前释放会被系统读到复用内存 → 0x80070002）
- **轮询式捕获**：弃用事件驱动（实测 loopback 事件句柄不置位），采用微软官方样例同款 `GetNextPacketSize` + 5ms 轮询，静音/活动期行为一致
- **混音节拍**：按输出环深度自 pacing，`timeBeginPeriod(1)` 保证 Sleep(1) 产能 ≈ 5× 实时，不全速自旋
- **格式协商**：请求引擎格式 → 不支持则回退设备 MixFormat + 转换（进程捕获/整系统捕获/输出三处同一条链）
- **默认设备切换**：未公开 COM 接口 `IPolicyConfig`（SoundSwitch 同款方案），Console/Multimedia/Communications 三角色一并设置
- **低延迟**：`GCLatencyMode.SustainedLowLatency` + 全链路预分配，音频回调内零分配

## 项目结构

```
SoundPipe.sln
├─ SoundPipe.Core/            引擎库（无 UI 依赖）
│  ├─ AudioEngine.cs          启停/双模式/异常恢复中枢
│  ├─ Audio/                  SpScRingBuffer、SampleConverter、DspMath、EngineFormat
│  ├─ Capture/                整系统循环捕获、进程循环捕获、麦克风捕获
│  ├─ Mixer/                  多源混音器（多输出环、线程安全增删源）
│  ├─ Output/                 OutputWriter（WASAPI 输出）、DriftCorrectedReader
│  ├─ Devices/                DeviceService（枚举/热插拔）、AutoSetup、PolicyConfig
│  ├─ Diagnostics/            LinkDiagnostics（回路测试）
│  ├─ Session/                SessionMonitor（发声应用枚举，1s 轮询）
│  ├─ Interop/                COM 接口自声明（IAudioCaptureClient、激活接口等）
│  ├─ Logging/                滚动文件日志（%APPDATA%\SoundPipe\logs）
│  └─ RestartPolicy.cs        流异常重启策略
├─ SoundPipe.App/             WPF 桌面端（两栏工作台 / 托盘常驻 / 设置持久化）
├─ SoundPipe.Cli/             命令行验证工具
├─ SoundPipe.Tests/           xUnit 单元测试（39 个）
└─ docs/superpowers/specs/    设计文档
```

## 测试

```powershell
.\dotnet.ps1 test SoundPipe.Tests
```

覆盖：环形缓冲并发、DSP（限幅/RMS/增益）、混音器多环、漂移修正与重启策略、采样转换器多格式大包。

## 故障排查

| 现象 | 处理 |
|---|---|
| 转发在跑但对方/录音听不到 | 先 `setup`（默认麦克风没指向虚拟录音端）；或用 `linkdiag` 定位 |
| 电平条不动 | 确认音源在播；apps 模式确认 PID 正确且进程在发声（`apps` 命令看 ●） |
| 声音发闷 / 破音 / 像隔层布 | 多半是音乐增益 >1.00：峰值进了软限幅区（-1dBFS 起压），动态被压扁且响度不增。调回 1.00，要更响请调系统或通话软件音量 |
| 日志里「强制补零」持续增长 | 源供数跟不上（进程被限流/设备时钟异常），听感是断续空洞；看「混音诊断」的目标环深度判断 |
| 链路诊断报"桥不通" | Voicemeeter 面板把虚拟输入条 B1 点亮；或换装 VB-CABLE 一劳永逸 |
| 自己扬声器没声音了 | 默认输出被切进虚拟设备，`setup` 会自动切回；也可手动到系统声音设置改回 |
| 对方听到回声 | 外放导致，戴耳机；或不要同时选两个麦克风 |
| 进程捕获激活失败/超时 | 需 Windows 10 2004+；目标进程退出会自动重启 3 次后弃源 |
| 查日志 | `%APPDATA%\SoundPipe\logs\soundpipe.log`（1MB × 5 个滚动） |

## 安全说明

日志只包含诊断信息，不记录任何音频数据。`setup` 只改动系统默认音频设备设置，可用系统设置随时改回。

## 许可证

[MIT](LICENSE)。可自由使用、修改与分发（含商用），请保留版权与许可声明。
