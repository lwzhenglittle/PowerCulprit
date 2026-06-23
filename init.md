# PowerCulprit 落地实现计划

你是一个本地代码 agent。请按本文档在当前目录开发一个 Windows 耗电分析工具：PowerCulprit。

目标不是写研究原型，而是交付一个可以构建、可以运行、可以采样、可以看到曲线和排行的 MVP。

## 0. 当前决策

以下决策已经固定，开发时不要重新发散：

* 语言：C#。
* 目标框架：.NET 8。
* 系统：Windows 10/11。
* UI：WinUI 3 / Windows App SDK。
* 图表：LiveCharts2 WinUI 适配包。
* 存储：SQLite。
* 日志：Microsoft.Extensions.Logging，必要时加 Serilog。
* MVVM：CommunityToolkit.Mvvm。
* 硬件方向：只面向 Intel CPU + Intel 核显 / Intel Arc / iGPU。
* 明确不做：NVIDIA、AMD、外接独显、NVML、nvidia-smi、AMD ADLX、AMD SMU。

## 1. 产品边界

本工具要回答：

> 最近一段时间，哪些进程和哪些硬件活动最像是在推高耗电？

本工具不能承诺：

> 每个进程真实消耗了多少瓦。

必须在 README 和 UI 中说明：

* 电池功率来自系统报告或传感器读数，读不到时使用容量变化估算。
* CPU/iGPU 功耗来自可用硬件传感器，读不到时为空。
* 进程耗电排行是基于 CPU/GPU/IO/前后台状态与整机放电曲线、硬件功耗曲线的相关性估计。
* 排行必须给出解释，不能只给一个分数。

## 2. 重要工程选择

### 2.1 App / CLI 拆分

不要把 WinUI 3 GUI、headless 采样、diagnose 输出全部塞进一个 WinExe。

采用两个入口：

* `PowerCulprit.Desktop`

  * WinUI 3 GUI。
  * 输出类型 `WinExe`。
  * 负责监控面板、曲线、进程表、排行、托盘。

* `PowerCulprit.Cli`

  * Console 应用。
  * 输出类型 `Exe`。
  * 负责：

    * `powerculprit`：启动 Desktop。
    * `powerculprit --headless --duration 30m`：无 GUI 采样。
    * `powerculprit --diagnose`：输出当前数据源和权限状态。

原因：WinUI 3 的 WinExe 不适合做可靠的控制台输出；CLI 单独做更容易落地。

### 2.2 WinUI 3 部署

默认选择 unpackaged WinUI 3，降低调试和本地运行门槛。

如果 Windows App SDK 模板或运行环境导致 unpackaged 阻塞，可以改成 MSIX packaged，但必须：

* 在 README 中说明部署方式。
* 验证进程枚举、Performance Counter、PDH、SQLite 文件路径、Win32 API 不被打包身份影响。

### 2.3 托盘

WinUI 3 没有 WPF 那种直接托盘能力。

实现优先级：

1. 先完成窗口常驻 + 后台采样。
2. 再用 Win32 `Shell_NotifyIcon` 或 WinForms `NotifyIcon` 互操作实现托盘。
3. 不要为了托盘把 UI 框架换成 WPF。

如果 MVP 时间不足，允许把托盘标为 TODO，但 GUI、采样、曲线、排行必须可用。

## 3. 项目结构

创建 solution：

```text
PowerCulprit.sln
```

项目：

```text
src/
  PowerCulprit.Core/
  PowerCulprit.Collectors/
  PowerCulprit.Storage/
  PowerCulprit.Desktop/
  PowerCulprit.Cli/
tests/
  PowerCulprit.Tests/
README.md
```

项目职责：

* `PowerCulprit.Core`

  * 领域模型。
  * 采样 DTO。
  * 时间窗口聚合。
  * 评分。
  * 排行解释生成。

* `PowerCulprit.Collectors`

  * BatteryPowerCollector。
  * ProcessResourceCollector。
  * WindowsGpuEngineCollector。
  * LibreHardwareMonitorCollector。
  * IntelCpuPowerCollector。
  * IntelGpuPowerCollector。
  * 数据源诊断。

* `PowerCulprit.Storage`

  * SQLite 初始化。
  * schema 创建 / 迁移。
  * 批量写入。
  * 查询最近窗口数据。
  * 历史数据清理。

* `PowerCulprit.Desktop`

  * WinUI 3 主窗口。
  * LiveCharts2 曲线。
  * 进程资源表。
  * 耗电嫌疑排行。
  * 数据源状态面板。
  * 托盘。

* `PowerCulprit.Cli`

  * 参数解析。
  * 启动 GUI。
  * headless 采样。
  * diagnose 输出。

* `PowerCulprit.Tests`

  * 核心算法和 fallback 测试。

## 4. 建议依赖

按需添加 NuGet：

```text
Microsoft.WindowsAppSDK
LiveChartsCore.SkiaSharpView.WinUI
CommunityToolkit.Mvvm
Microsoft.Data.Sqlite
Microsoft.Extensions.Hosting
Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Logging
Microsoft.Extensions.Logging.Console
LibreHardwareMonitorLib
xunit
xunit.runner.visualstudio
Microsoft.NET.Test.Sdk
```

如果 `System.CommandLine` 稳定版本不方便，CLI 参数可以先手写解析，只支持本文档列出的少量参数。

## 4.1 开发期运行命令

开发期不要求一开始就有全局 `powerculprit` 命令。

开发时使用：

```powershell
dotnet run --project src\PowerCulprit.Cli
dotnet run --project src\PowerCulprit.Cli -- --diagnose
dotnet run --project src\PowerCulprit.Cli -- --headless --duration 30s
dotnet run --project src\PowerCulprit.Desktop
```

最终交付或 publish 后，再把 CLI 入口作为 `powerculprit` 使用。

如果 WinUI 3 模板不可用：

* 先确认 .NET 8 SDK、Windows App SDK 模板、Windows desktop build tooling 是否安装。
* 不要改回 WPF 或 Avalonia。
* 可以手工创建 WinUI 3 项目文件，但必须保留 `PowerCulprit.Desktop` 作为 WinUI 3 桌面项目。

## 5. 数据模型

先实现这些核心 record/class。

### 5.1 SystemPowerSample

字段：

```text
TimestampUtc
IsAcOnline
BatteryPercent nullable
ChargeRateMilliwatts nullable
RemainingCapacityMWh nullable
FullChargeCapacityMWh nullable
EstimatedDischargeWatts nullable
PowerMode nullable
```

说明：

* 放电时 `ChargeRateMilliwatts` 可能为负。
* UI 中展示放电率 W 时，优先使用 `abs(ChargeRateMilliwatts) / 1000`。
* 如果 ChargeRate 不可用，则使用 `EstimatedDischargeWatts`。

### 5.2 ProcessSample

字段：

```text
TimestampUtc
Pid
ProcessName
ExecutablePath nullable
CommandLine nullable
ParentPid nullable
CpuPercent nullable
WorkingSetMb nullable
PrivateMemoryMb nullable
ThreadCount nullable
HandleCount nullable
DiskReadBytesPerSecond nullable
DiskWriteBytesPerSecond nullable
NetworkReceiveBytesPerSecond nullable
NetworkSendBytesPerSecond nullable
IsForegroundProcess
```

### 5.3 GpuProcessSample

字段：

```text
TimestampUtc
Pid nullable
ProcessName nullable
EngineName
EngineType
UtilizationPercent
```

EngineType：

```text
3D
Compute
VideoDecode
VideoEncode
Copy
Other
```

### 5.4 HardwareSensorSample

字段：

```text
TimestampUtc
Source
DeviceName
SensorName
MetricName
Value
Unit
```

Source 示例：

```text
LibreHardwareMonitor
WindowsGpuEngine
IntelPowerGadget
IntelPCM
LevelZeroSysman
```

### 5.5 SourceStatus

字段：

```text
TimestampUtc
SourceName
IsAvailable
Status
Details nullable
RequiresAdmin nullable
```

用于 UI 和 `--diagnose`。

### 5.6 CulpritReportItem

字段：

```text
ProcessName
Pid nullable
Score
Rank
AvgCpuPercent nullable
MaxCpuPercent nullable
AvgGpuPercent nullable
MaxGpuPercent nullable
DiskMb nullable
NetworkMb nullable
BackgroundActiveSeconds nullable
PowerCorrelation nullable
CpuPowerCorrelation nullable
GpuActivityCorrelation nullable
Reason
```

## 6. SQLite Schema

默认数据库路径：

```text
%LocalAppData%\PowerCulprit\powerculprit.db
```

日志路径：

```text
%LocalAppData%\PowerCulprit\logs
```

最少建表：

```text
system_power_samples
process_samples
gpu_process_samples
hardware_sensor_samples
analysis_reports
source_status
```

要求：

* 启动时自动创建表。
* 写入使用批量事务。
* 采样失败只记录日志和 status，不中断主循环。
* 提供清理历史数据方法，默认保留最近 7 天。
* 时间统一存 UTC ISO-8601 或 Unix milliseconds，项目内保持一致。

## 7. 采集器实现计划

所有采集器都要遵守：

* 失败不抛到主循环。
* 权限不足时字段置 null 或返回 SourceStatus。
* 不支持的数据源标记 unavailable。
* 每个采集器提供 `CollectAsync` 或同步 `Collect`，由采样循环统一调度。

### 7.1 BatteryPowerCollector

必须实现：

* 使用 Windows API / WinRT 获取：

  * 是否接 AC。
  * 电池百分比。
  * 估计剩余时间。
  * 是否省电模式。

* 尝试使用：

```text
Windows.Devices.Power.Battery.AggregateBattery.GetReport()
```

读取：

```text
ChargeRateInMilliwatts
FullChargeCapacityInMilliwattHours
RemainingCapacityInMilliwattHours
DesignCapacityInMilliwattHours
```

fallback：

* 如果 ChargeRate 不可用，用两次 RemainingCapacity 差值估算 W。
* 如果没有电池，返回 Battery API unavailable，不崩溃。

### 7.2 ProcessResourceCollector

必须实现：

* 枚举进程。
* 两次采样 delta 计算 CPU 使用率。
* 记录内存、线程数、句柄数。
* 尝试读取路径、命令行、父 PID，失败为 null。
* 尝试读取进程 IO counter，计算磁盘读写 bytes/s。
* 进程级网络先允许为 null。
* 判断当前前台进程，设置 `IsForegroundProcess`。

注意：

* 进程可能在采样中退出。
* 某些系统进程会拒绝访问。
* 不要因为单个进程失败影响整轮采样。

### 7.3 WindowsGpuEngineCollector

必须实现：

* 使用 Windows Performance Counter / PDH 读取 `GPU Engine`。
* 解析 instance 中的 PID。
* 聚合到 `GpuProcessSample`。
* 映射 engine 类型：3D、Compute、VideoDecode、VideoEncode、Copy、Other。

fallback：

* 如果 counter 不存在，SourceStatus 标记 unavailable。
* 如果 PID 解析失败，PID 允许为 null。

### 7.4 LibreHardwareMonitorCollector

建议实现：

* 使用 LibreHardwareMonitorLib。
* 打开：

```text
IsCpuEnabled
IsGpuEnabled
IsMemoryEnabled
IsMotherboardEnabled
IsBatteryEnabled
```

* 递归读取 hardware/subhardware sensors。
* 记录 Power、Temperature、Load、Clock、Voltage、Energy、Current。

重点关注：

* CPU Package。
* IA Cores。
* GT / Graphics。
* Intel GPU。
* Battery。

fallback：

* 没管理员权限时可能读不到部分传感器，不能崩溃。
* 读不到功耗时状态说明，而不是造假数据。

### 7.5 IntelCpuPowerCollector

实现策略：

1. 从 LibreHardwareMonitorCollector 的 samples 中筛选 CPU power/load/temp/clock。
2. 如果本机存在 Intel Power Gadget / PowerLog，再做适配。
3. 如果本机存在 Intel PCM，再做适配或留清晰接口。
4. 都不可用时返回 null + SourceStatus。

MVP 不要求直接读 MSR/RAPL。

### 7.6 IntelGpuPowerCollector

实现策略：

1. 从 LibreHardwareMonitorCollector 中筛选 Intel GPU power/load/temp/clock。
2. 使用 WindowsGpuEngineCollector 作为 per-process GPU activity fallback。
3. Level Zero Sysman 只做探测或预留接口；除非很容易，否则不要卡在这里。

## 8. 采样服务

实现 `MonitoringService`。

职责：

* 默认每 2 秒采样一次。
* 支持配置 1 到 5 秒。
* 调用各采集器。
* 批量写入 SQLite。
* 将最新快照发布给 UI。
* 定期刷新数据源状态。
* 捕获异常并写日志。

接口建议：

```text
StartAsync()
StopAsync()
GetLatestSnapshot()
SetInterval(seconds)
```

UI 和 CLI 都复用这个服务。

## 9. 分析器

实现 `PowerCulpritAnalyzer`。

输入：

```text
TimeSpan window
int top
```

输出：

```text
IReadOnlyList<CulpritReportItem>
```

MVP 评分可以简单但必须可解释。

建议基础分：

```text
CPU 分数 = avg_cpu_percent * 1.5 + max_cpu_percent * 0.3
GPU 分数 = avg_gpu_percent * 2.0 + max_gpu_percent * 0.4
VideoDecode/VideoEncode 活跃加权
后台活跃加权
磁盘 IO 中等加权
相关性正向加权
```

缺失数据处理：

* 没有 CPU package power，不影响 CPU/GPU/IO 排行。
* 没有 iGPU power，用 GPU Engine utilization 归因。
* 没有电池功率，仍可按资源活跃排行，但 Reason 要说明缺少整机放电相关性。

Reason 示例：

```text
chrome.exe：过去 30 分钟平均 CPU 12.3%，GPU VideoDecode 活跃 18.6%，后台活跃 22 分钟；放电率升高期间该进程资源曲线同步上升，相关性 0.71，因此排名第 1。
```

## 10. WinUI 3 界面

第一屏必须是实际监控面板。

不要做欢迎页、营销页或空壳页面。

### 10.1 顶部状态栏

显示：

* 电池百分比。
* 接电状态。
* 当前放电率 W。
* CPU package power W，如果可用。
* Intel iGPU activity / power，如果可用。
* 采样状态。

### 10.2 曲线区

使用 LiveCharts2。

至少显示：

* 电池百分比曲线。
* 放电率曲线。
* CPU package power 曲线，可为空。
* Intel iGPU / GPU Engine activity 曲线，可为空。

时间窗口：

```text
5 min
15 min
30 min
60 min
```

### 10.3 进程资源表

列：

```text
PID
ProcessName
CPU %
GPU %
Memory MB
Disk Read/s
Disk Write/s
Foreground
Score
Reason
```

要求：

* 默认按 Score 或 CPU/GPU 活跃度排序。
* 缺失值显示为 `--`。
* 不要让表格因为长命令行或长进程名撑坏布局。

### 10.4 数据源状态面板

显示：

* Battery API。
* ChargeRateInMilliwatts。
* Windows GPU Engine counter。
* LibreHardwareMonitor。
* CPU package power。
* Intel iGPU power。
* 当前是否管理员。

状态：

```text
Available
Unavailable
Partial
Requires admin
```

### 10.5 托盘

菜单：

```text
Show Dashboard
Start Monitoring
Stop Monitoring
Export Data
Exit
```

行为：

* 关闭窗口时隐藏窗口，不退出进程。
* Exit 才真正退出。
* 如果托盘暂时没完成，README 标记 TODO，但不要影响主监控面板运行。

## 11. CLI

只实现最小能力。

### 11.1 默认启动 GUI

```powershell
powerculprit
```

行为：

* 启动 `PowerCulprit.Desktop.exe`。

### 11.2 Headless 采样

```powershell
powerculprit --headless --duration 2m
powerculprit --headless --duration 30m --interval 2
powerculprit --headless --duration 30m --interval 2 --db C:\temp\powerculprit.db
```

要求：

* 在控制台输出采样开始、数据库路径、结束原因。
* 生成 SQLite 数据。
* Ctrl+C 能优雅停止并 flush。

### 11.3 Diagnose

```powershell
powerculprit --diagnose
```

输出：

* Windows 版本。
* CPU 名称。
* GPU 名称。
* Battery API 状态。
* ChargeRateInMilliwatts 状态。
* Windows GPU Engine counter 状态。
* LibreHardwareMonitor 状态。
* Intel Power Gadget / PowerLog 是否存在。
* Intel PCM 是否存在。
* Level Zero Sysman 是否存在。
* 是否管理员权限。
* 默认数据库路径。
* 日志路径。

## 12. 测试计划

至少实现这些测试：

* CPU 使用率 delta 计算。
* 放电速度估算。
* 时间窗口聚合。
* 评分排序。
* 空数据不崩溃。
* 缺失电池功率时仍能排行。
* 缺失 CPU package power 时 fallback。
* 缺失 iGPU power 时使用 GPU Engine activity。
* 进程结束后仍能分析历史样本。
* GPU Engine 样本聚合。

不要为 WinUI 3 UI 写复杂自动化测试；MVP 阶段以 Core / Collectors / Storage 的单元测试为主。

## 13. README 要求

README.md 必须包含：

* 项目用途。
* 当前 MVP 功能。
* 如何构建。
* 如何运行 GUI。
* 如何 headless 采样。
* 如何 diagnose。
* 数据库和日志路径。
* 支持的数据源。
* 哪些功率是实际读数，哪些是估算。
* 排行榜如何解释。
* Intel CPU / Intel iGPU / Intel Arc 支持说明。
* 已知限制。
* TODO。

必须明确写：

* 本工具不能保证得到每个进程真实耗电瓦数。
* Intel iGPU 精确功率可能读不到，此时使用 Windows GPU Engine utilization 做归因。
* 不同 BIOS、驱动、Windows 版本会影响可读取数据。
* 当前版本不实现 NVIDIA/AMD/独显路径。

## 14. 开发阶段

按顺序执行。每个阶段完成后尽量运行 build/test。

### Phase 1：创建工程

任务：

* 创建 solution。
* 创建 Core / Collectors / Storage / Desktop / Cli / Tests。
* 设置项目引用。
* 添加基础 NuGet。
* 确认 `dotnet build` 能跑到可诊断状态。

验收：

```powershell
dotnet build
```

### Phase 2：Core + Storage

任务：

* 实现模型。
* 实现 SQLite schema。
* 实现数据库初始化。
* 实现批量写入。
* 实现最近窗口查询。

验收：

* 单元测试能验证 schema 创建和基础读写。

### Phase 3：电池 + 进程采样

任务：

* BatteryPowerCollector。
* ProcessResourceCollector。
* CPU delta 计算。
* IO delta 计算。
* 前台进程判断。
* MonitoringService 基础循环。

验收：

```powershell
powerculprit --headless --duration 30s
```

应生成 SQLite 数据。

### Phase 4：WinUI 3 MVP

任务：

* 主窗口。
* 顶部状态栏。
* 进程表。
* 电池百分比曲线。
* 放电率曲线。
* 数据源状态基础显示。

验收：

```powershell
powerculprit
```

应打开 GUI，并能看到电池、放电率、进程资源。

### Phase 5：GPU Engine + 硬件传感器

任务：

* WindowsGpuEngineCollector。
* LibreHardwareMonitorCollector。
* IntelCpuPowerCollector。
* IntelGpuPowerCollector。
* 数据源状态完善。

验收：

```powershell
powerculprit --diagnose
```

应列出当前机器哪些数据源可用、哪些不可用、原因是什么。

### Phase 6：分析排行

任务：

* PowerCulpritAnalyzer。
* 窗口聚合。
* 评分。
* Reason 生成。
* UI 排行榜。

验收：

* GUI 中能看到耗电嫌疑排行。
* 缺失硬件功率数据时仍能生成排行，并说明缺失数据。

### Phase 7：托盘 + 导出 + README

任务：

* 托盘图标和菜单。
* Export Data 按钮，至少导出 CSV。
* README。
* 清理 TODO 和明显日志噪音。

验收：

* 关闭窗口后应用继续采样。
* 托盘 Show Dashboard 可以恢复窗口。
* Exit 可以退出。

## 15. 最终验收标准

必须满足：

1. `dotnet build` 通过。
2. `dotnet test` 通过。
3. `powerculprit` 能打开 WinUI 3 监控面板。
4. UI 显示电池百分比、接电状态、放电率或估算放电率。
5. UI 显示电池百分比和放电率随时间变化。
6. UI 显示各进程 CPU、内存、磁盘、GPU Engine 使用情况，缺失字段允许为空。
7. UI 显示 CPU package power / Intel iGPU power 或不可用原因。
8. UI 显示耗电嫌疑排行和解释。
9. `powerculprit --headless --duration 2m` 能生成 SQLite 数据。
10. `powerculprit --diagnose` 能列出当前可用的数据源和权限状态。
11. 没有电池、没有 iGPU power sensor、没有管理员权限时程序不会崩溃。
12. README 写清楚限制和使用方法。
13. 代码中没有 NVIDIA/AMD 采集器实现。

## 16. 当前机器已知情况

之前的浅层检查结果：

* 当前目录没有现成项目，只有 `init.md` 和 `.git`。
* 机器是 Windows 11。
* CPU 是 Intel。
* GPU 是 Intel Arc。
* 有电池。
* `GPU Engine` performance counter 存在。
* 当前沙箱命令不是管理员。
* 当前机器之前检查到只有 .NET runtime，没有 .NET SDK。

如果开始开发时仍然没有 .NET SDK，需要先安装 .NET 8 SDK；否则无法 `dotnet new` / `dotnet build` / `dotnet test`。

## 17. 开发注意事项

* 优先做可运行链路，不要一开始追求所有硬件功耗都读到。
* 硬件传感器读不到是正常情况，要显示 unavailable/partial。
* 不要为不存在的数据编造功率值。
* 采样循环不能因为单个采集器失败中断。
* UI 不要阻塞采样线程。
* SQLite 写入要批量事务。
* 所有时间使用 UTC 存储。
* 进程访问失败、进程退出、权限不足都要静默降级并记录日志。
* 不要引入 NVIDIA/AMD 相关代码。
