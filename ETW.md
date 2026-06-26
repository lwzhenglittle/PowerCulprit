你是本地代码 agent，请在当前仓库 `PowerCulprit` 中实现一个初步的 Windows ETW 接入方案。目标不是重写整个采集系统，而是在现有轮询采集基础上增加 ETW 增量采集能力，用于补齐进程生命周期、进程级网络活动，并为后续磁盘 IO / CPU 唤醒分析预留接口。

> 本文是后续实现用的方案文档。当前阶段只完善方案，不进行代码实现。

## 已确认决策

本方案按以下决策执行，不再反复讨论：

1. **ETW 第一版默认开启。**
   * `MonitoringService` 启动时自动尝试启动 ETW collector。
   * ETW 启动失败、权限不足、provider 启用失败、session 冲突，都必须降级为 `SourceStatus`，不能影响主功能启动。
   * 用户不需要管理员权限才能使用 PowerCulprit 主功能；非管理员下 ETW 可以显示 `Requires admin` / `Unavailable` / `Partial`。
2. **网络采集第一版 TCP 优先。**
   * 第一版只要求稳定接入 TCP/IP send/receive 事件并聚合 bytes。
   * UDP 作为后续增强，不作为第一版验收条件。
   * 网络字段必须标注为 ETW TCP/IP 事件的进程级活动估算，不承诺覆盖所有网络路径。
3. **`--diagnose` 使用短时 Probe。**
   * 单独设计短时探测逻辑，例如 `WindowsEtwActivityProbe`。
   * 诊断模式不要长期启动 ETW collector，也不要长时间消费事件。
   * Probe 必须快速返回，并在 `finally` 中停止/释放它创建的 session。

## 背景和约束

项目是一个 Windows 电池耗电分析工具：

* 语言：C#
* 目标框架：.NET 10
* 系统：Windows 10/11
* UI：WinUI 3 / Windows App SDK
* 存储：SQLite
* 采集层：`src/PowerCulprit.Collectors`
* 核心模型：`src/PowerCulprit.Core`
* CLI：`src/PowerCulprit.Cli`
* GUI：`src/PowerCulprit.Desktop`

固定约束：

* 不要加入 NVIDIA / AMD / NVML / ADLX / SMU 代码路径。
* 不要把项目改成 WPF / Avalonia。
* 不要承诺真实 per-process wattmeter。
* 缺失数据必须为 `null`，不能编造。
* 任何 collector 出错都不能中断 `MonitoringService` 主循环。
* 如果增加新的 collector，需要同时在 Desktop 的 `App.xaml.cs` 和 CLI 的 `Program.cs` 注册。
* 不要保存 raw ETW event 到 SQLite，只保存每轮聚合后的结果。
* ETW 结果是辅助归因信号，不改变 PowerCulprit “相关性和归因工具，而非 per-process wattmeter” 的产品定位。

## 总体目标

实现一个初步 ETW 增量采集模块：

```text
WindowsEtwActivityCollector
    默认随 MonitoringService 启动而尝试启动
    后台启动 ETW session
    实时消费 ProcessStart / ProcessStop / TCPIP send/receive 事件
    按 PID 聚合活动
    每个 MonitoringService 采样周期 SnapshotAndReset
    merge 到现有 ProcessSample
```

当前阶段只要求：

1. 接入 ETW session 生命周期。
2. 采集 ProcessStart / ProcessStop，维护 PID 元数据缓存。
3. 尽量采集 TCP/IP send/receive 事件，聚合进程级网络收发 bytes。
4. 把聚合结果 merge 到 `ProcessSample.NetworkReceiveBytesPerSecond` 和 `ProcessSample.NetworkSendBytesPerSecond`。
5. 增加 diagnose / SourceStatus 输出，说明 ETW 是否可用、是否需要管理员权限、是否处于 Partial。
6. 保留现有 `ProcessResourceCollector` 的 CPU、内存、线程、句柄、粗略磁盘 IO 轮询逻辑。
7. ETW 默认开启，但必须完全可降级。

## 不做的事情

本阶段不要做：

* 不要用 ETW 替换 `NtQuerySystemInformation`。
* 不要用 ETW 替换 Battery API。
* 不要用 ETW 替换 LibreHardwareMonitor。
* 不要用 ETW 替换 Windows GPU Engine Performance Counter。
* 不要实现 GPU/DxgKrnl ETW。
* 不要实现 UDP 采集作为第一版必需功能。
* 不要保存原始 ETW 事件。
* 不要让 ETW 失败导致程序启动失败。
* 不要要求用户必须管理员才能使用主功能。
* 不要为了清理 session 而停止不是本进程创建的 ETW session。
* 不要把网络 bytes 解释成直接功耗，也不要在 UI/报告里暗示 per-process wattmeter。

## 推荐依赖

在 `src/PowerCulprit.Collectors/PowerCulprit.Collectors.csproj` 增加 TraceEvent 依赖：

```xml
<PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="<稳定版本>" />
```

要求：

* 优先 pin 一个当前 NuGet 稳定版本，避免 TraceEvent parser API 或 payload 字段变化导致未来构建漂移。
* 如果为了保持仓库现有风格暂时使用 `Version="*"`，最终总结必须说明实际 restore 到的版本，并建议后续 pin。
* 不要把 TraceEvent 依赖加入 Core；ETW 是 Windows collector 细节，应留在 `PowerCulprit.Collectors`。

## 新增文件建议

新增目录：

```text
src/PowerCulprit.Collectors/Etw/
```

新增文件：

```text
src/PowerCulprit.Collectors/Etw/WindowsEtwActivityCollector.cs
src/PowerCulprit.Collectors/Etw/WindowsEtwActivityProbe.cs
src/PowerCulprit.Collectors/Etw/EtwProcessActivitySnapshot.cs
src/PowerCulprit.Collectors/Etw/EtwProcessActivityAccumulator.cs
src/PowerCulprit.Collectors/Etw/ProcessEtwMerger.cs
```

如果命名或目录和现有风格冲突，可以调整，但职责要保持清晰。

## 数据结构设计

实现一个聚合结果模型，例如：

```csharp
namespace PowerCulprit.Collectors.Etw;

public sealed record EtwProcessActivitySnapshot
{
    public DateTime TimestampUtc { get; init; }
    public TimeSpan Interval { get; init; }
    public bool TcpProviderAvailable { get; init; }
    public bool HadLostEvents { get; init; }
    public IReadOnlyDictionary<int, EtwProcessActivity> Processes { get; init; }
        = new Dictionary<int, EtwProcessActivity>();
}

public sealed record EtwProcessActivity
{
    public int Pid { get; init; }
    public string? ProcessName { get; init; }
    public string? ImagePath { get; init; }
    public string? CommandLine { get; init; }
    public int? ParentPid { get; init; }
    public DateTime? StartTimeUtc { get; init; }
    public DateTime? StopTimeUtc { get; init; }

    public long NetworkReceiveBytes { get; init; }
    public long NetworkSendBytes { get; init; }
    public bool HasNetworkActivity { get; init; }

    public long DiskReadBytes { get; init; }
    public long DiskWriteBytes { get; init; }

    public long ProcessStartCount { get; init; }
    public long ProcessStopCount { get; init; }
}
```

设计说明：

* 第一阶段可以先让 `DiskReadBytes` / `DiskWriteBytes` 保持 0，作为后续预留。
* `TcpProviderAvailable` 用于区分“TCP provider 不可用”和“本窗口没有捕获到某 PID 网络事件”。
* `HasNetworkActivity` 用于避免把“没有事件”误写成 0。第一版只在该 PID 本窗口确实观察到 TCP send/receive 事件时写网络速率。
* `HadLostEvents` 用于未来从 ETW buffer callback / TraceEvent 状态中反映事件丢失；第一版取不到也可以保守为 false，但不要伪造精确性。

## 网络字段语义

第一版网络数据是 **ETW TCP/IP events based process activity estimate**，不是完整网络计量器。

必须遵守：

1. 只采集 TCP/IP send/receive 事件作为第一版范围。
2. 优先使用事件 payload 中的 `ProcessId`，不要盲目使用 ETW header 的进程 ID。
3. 如果事件没有可靠 PID，或 PID <= 0，忽略。
4. 如果字段名在不同 Windows / TraceEvent 版本中不同，写兼容代码；无法可靠解析时保守忽略该事件，并把状态降为 `Partial`。
5. 未观察到某 PID 的 TCP send/receive 事件时，不要把网络速率写成 0；保持原值，通常为 `null`。
6. 只有当该 PID 在本 ETW 窗口内有明确 TCP send/receive bytes 时，才写入 `NetworkReceiveBytesPerSecond` / `NetworkSendBytesPerSecond`。
7. 如果 ETW 事件丢失，状态应反映可能不完整，不能把结果描述成完整网络计量。
8. 后续如果引入 UDP，应单独扩展状态和测试，不影响第一版 TCP 优先验收。

## Process metadata 语义

ETW ProcessStart / ProcessStop 维护的是补充 metadata cache，不是第一版 WMI 替代品。

必须遵守：

* 启动 ETW collector 之前已经存在的进程可能没有完整 ETW metadata。
* 仍保留 `ProcessResourceCollector` 当前的 30 秒 WMI 缓存，用于 command line / parent PID / executable path fallback。
* `ProcessEtwMerger` 只能在现有 `ProcessSample.CommandLine` / `ExecutablePath` / `ParentPid` 为空时，用 ETW 非空值补齐。
* ETW 空值不能覆盖现有非空值。
* ProcessStop 后不要立即删除 metadata，至少保留一个短 TTL，避免短生命周期进程在分析窗口中失去名称。
* 注意 PID reuse：ProcessStart 应更新该 PID 的 metadata；长期缓存必须有 TTL 或 start time 校验，避免旧 PID metadata 污染新进程。

## WindowsEtwActivityCollector 行为

实现一个 singleton collector：

```csharp
public sealed class WindowsEtwActivityCollector : IDisposable
{
    public bool IsRunning { get; }
    public SourceStatus GetStatus();

    public Task StartAsync(CancellationToken cancellationToken = default);
    public Task StopAsync();

    public EtwProcessActivitySnapshot SnapshotAndReset(DateTime nowUtc);
}
```

要求：

1. `StartAsync`：

   * 默认由 `MonitoringService` 调用并尝试启动。
   * 创建实时 ETW session。
   * 后台线程 / task 运行 `session.Source.Process()`。
   * 启用 kernel providers：

     * Process
     * TCP/IP Network，具体 TraceEvent keyword/API 以实际版本为准，例如 `NetworkTCPIP` / `TcpIp`。

   * `StartAsync` 只负责启动后台消费任务，必须快速返回；不能把 `session.Source.Process()` 放在调用线程阻塞执行。
   * 如果权限不足、provider 不可用、session 创建失败、session 已存在、provider enable 失败：

     * 记录日志。
     * 设置 SourceStatus 为 `Unavailable` / `Requires admin` / `Partial`。
     * 不抛出到上层。
     * 不影响 `MonitoringService` 启动。

2. `StopAsync`：

   * 请求停止 session。
   * 尽量优雅停止后台处理线程。
   * Dispose session。
   * 只停止本 collector 当前进程创建的 session。
   * 不要停止未知来源或其他进程创建的 ETW session。
   * 不因停止失败导致主程序崩溃。

3. `SnapshotAndReset`：

   * 返回上一个采样周期内的聚合数据。
   * 重置周期内计数器。
   * 保留 PID 元数据缓存，不要因为 reset 丢掉 process name / parent pid / command line。
   * 线程安全。
   * 如果 ETW 未运行或不可用，返回空 snapshot，并让 SourceStatus 说明原因。

4. 后台处理任务：

   * 所有事件 callback 必须 catch 局部异常，不能让单个 malformed event 杀死消费循环。
   * 后台 task 顶层必须 catch 异常，记录日志并更新 SourceStatus。
   * 如果实时消费结束，`IsRunning` 和 SourceStatus 应反映已停止/不可用。

5. Session 命名和冲突：

   * 使用明确的 PowerCulprit 专用 session 名称。
   * session 名称是机器级资源；如果创建失败或冲突，不要强行停止未知 session。
   * 可以选择带进程 ID 的唯一 session 名，降低多实例冲突；如果这样做，必须确保 `StopAsync` / `Dispose` 清理本实例 session。
   * 任何 session 残留清理策略都必须保守，不能影响 WPR、PerfView、GPUView 或其他工具。

6. SourceStatus：

   * `SourceName = "WindowsETW"`
   * Status 可以是：

     * `Available`
     * `Partial`
     * `Unavailable`
     * `Disabled`
     * `Requires admin`

   * Details 说明：

     * 已启用 provider：Process、TCP/IP。
     * 哪些 provider 失败。
     * 是否需要管理员。
     * 是否发生 session 冲突。
     * 是否检测到事件丢失或解析失败。

   * 权限不足时：`Status = "Requires admin"`，`RequiresAdmin = true`。
   * ETW 默认开启，所以 `Disabled` 只用于未来显式关闭或配置禁用。

## WindowsEtwActivityProbe 行为

`--diagnose` 不直接复用长期 collector，而是使用短时 probe。

建议接口：

```csharp
public static class WindowsEtwActivityProbe
{
    public static Task<IReadOnlyList<SourceStatus>> ProbeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
```

要求：

* 使用 probe 专用 session 名称。
* 最多短暂尝试创建 session、启用 Process provider、启用 TCP/IP provider，然后立即停止。
* 不需要长期调用 `session.Source.Process()`；如果必须消费，时间应非常短，例如 1 秒以内。
* 所有失败都转成诊断输出，不抛出到 CLI 顶层。
* `finally` 中停止并释放 probe 创建的 session。
* 不要清理不是 probe 本次创建的 session。
* 诊断输出应区分：

```text
ETW Kernel Session: Available / Requires admin / Unavailable
ETW Process Events: Available / Partial / Requires admin
ETW TCPIP Events: Available / Partial / Requires admin
```

如果静态检查和真实 probe 结果不一致，以真实 probe 结果为准。

## ETW 事件处理要求

ProcessStart：

* 记录 PID。
* 记录 ProcessName / ImagePath / CommandLine / ParentPid / StartTimeUtc，能取到多少取多少。
* 更新 PID 元数据缓存。
* `ProcessStartCount++`。
* 如果该 PID 已有旧 metadata，按新的 start event 替换，避免 PID reuse 污染。

ProcessStop：

* 记录 PID。
* 记录 StopTimeUtc。
* `ProcessStopCount++`。
* 不要立即删除 PID 元数据；保留一段时间，避免短生命周期进程在分析窗口中失去名称。

TCP/IP send/receive：

* 按 PID 聚合 bytes。
* send 写入 `NetworkSendBytes`。
* receive 写入 `NetworkReceiveBytes`。
* 设置该 PID 的 `HasNetworkActivity = true`。
* 如果事件没有可靠 PID 或 PID <= 0，忽略。
* 如果事件字段名在不同 Windows/TraceEvent 版本中不同，需要写兼容代码或保守 fallback。
* 如果 TCP/IP provider 启用失败，整体 ETW collector 可以保持 `Partial`，而不是 `Unavailable`，只要 Process provider 仍可用。
* 如果 Process provider 失败但 TCP/IP provider 可用，也可以保持 `Partial`；网络 bytes 仍可聚合，但 metadata 可能缺失。

## EtwProcessActivityAccumulator

建议单独实现 accumulator，方便测试且避免真实 ETW session 参与单元测试。

职责：

* 线程安全地接收 process start / stop / TCP send / TCP receive。
* 维护周期计数器。
* 维护跨周期 PID metadata cache。
* `SnapshotAndReset(nowUtc)` 返回窗口数据并清空周期 bytes/count。
* `SnapshotAndReset` 不清空 metadata cache。
* 定期 prune metadata cache，避免无限增长。

建议规则：

* metadata TTL 可先设为 10–30 分钟，或根据 stop time 后保留几分钟。
* cache prune 不影响当前 snapshot 中仍有活动的 PID。
* 所有 public 方法都必须线程安全。
* 对负 bytes、异常 PID、无效 timestamp 保守忽略。

## 和 ProcessSample 合并

实现 `ProcessEtwMerger`，职责是把 ETW 聚合结果合并到现有 `ProcessSample` 列表。

建议接口：

```csharp
public static class ProcessEtwMerger
{
    public static IReadOnlyList<ProcessSample> Merge(
        IReadOnlyList<ProcessSample> processSamples,
        EtwProcessActivitySnapshot etwSnapshot);
}
```

合并规则：

1. 如果 `ProcessSample` 中已有 PID：

   * 如果 ETW 中该 PID 有明确 TCP 网络 activity，且 interval 合法，则换算成 bytes/s：

     * `receiveBytes / interval.TotalSeconds`
     * `sendBytes / interval.TotalSeconds`

   * 写入 `NetworkReceiveBytesPerSecond`。
   * 写入 `NetworkSendBytesPerSecond`。
   * 如果 `CommandLine` / `ExecutablePath` / `ParentPid` 原来为空，而 ETW 有非空值，可以补上。

2. 如果 ETW 中没有该 PID 的网络 activity：

   * 不要把网络速率写成 0。
   * 保持原 `ProcessSample` 字段不变，通常仍为 `null`。

3. 如果 ETW 里有短生命周期进程，但当前轮询快照已经看不到：

   * 第一阶段不创建新的 `ProcessSample`，避免引入 “exited process sample” 语义复杂度。
   * 后续再考虑创建 synthetic exited-process sample。

4. 如果 interval 非法或小于等于 0：

   * 不合并网络速率。
   * 可以合并非空 metadata。
   * 不抛异常。

5. 如果 ETW snapshot 标记 `HadLostEvents = true`：

   * 仍可合并已有 bytes，但 SourceStatus / Details 应反映结果可能不完整。
   * 不要在 Reason/UI 中描述为完整计量。

6. Record 语义：

   * `ProcessSample` 是 record，应通过 `sample with { ... }` 创建新对象。
   * 不要修改输入列表。

## 修改 MonitoringService

在 `MonitoringService` 中注入 `WindowsEtwActivityCollector`。

启动时：

* `StartAsync` 中默认启动 ETW collector。
* `WindowsEtwActivityCollector.StartAsync` 必须快速返回，只启动后台 ETW 消费任务。
* 如果 ETW 启动失败，由 collector 内部降级，不影响 monitoring service。
* 不要让 ETW session 启动卡住 UI 或 CLI headless 启动。

停止时：

* `StopAsync` 中停止 ETW collector。
* 停止失败只记录日志，不影响主服务停止。

每轮采样时：

当前大致流程是：

```text
Collect battery
Collect processes
Collect GPU Engine
Collect LHM
Derive CPU / iGPU
Refresh source statuses
Write database
Publish snapshot
```

修改为：

```text
Collect battery
Collect processes
SnapshotAndReset ETW activity
Merge ETW activity into processSamples
Collect GPU Engine
Collect LHM
Derive CPU / iGPU
Refresh source statuses
Write database
Publish snapshot
```

注意：

* ETW collector 异常必须 catch 并记录日志。
* 如果 ETW 不可用，`SnapshotAndReset` 返回空结果。
* 不要改变现有 GPU 采样开关逻辑。
* 不要改变现有数据库写入队列结构，除非确实需要。
* `RefreshSourceStatuses` 中加入 ETW status。
* ETW 默认开启，但状态必须清楚显示 `Available` / `Partial` / `Requires admin` / `Unavailable`。

## 修改 DI 注册

在 Desktop：

```text
src/PowerCulprit.Desktop/App.xaml.cs
```

注册：

```csharp
services.AddSingleton<WindowsEtwActivityCollector>();
```

在 CLI headless：

```text
src/PowerCulprit.Cli/Program.cs
```

注册同样的 singleton。

如果 namespace 是 `PowerCulprit.Collectors.Etw`，记得补 using。

由于 ETW 第一版默认开启，DI 注册后 `MonitoringService` 应默认使用它；但 ETW collector 自身必须能在失败时安全降级。

## 修改 CLI diagnose

在 `RunDiagnoseAsync()` 中增加 ETW 检查输出。

要求输出类似：

```text
ETW Kernel Session: Available
ETW Process Events: Available
ETW TCPIP Events: Available / Partial / Requires admin
```

实现要求：

* 使用 `WindowsEtwActivityProbe`，不要长期启动 `WindowsEtwActivityCollector`。
* Probe 必须快速返回。
* Probe 创建的 session 必须在 `finally` 中停止/释放。
* 不要在 diagnose 中长时间运行 ETW session。
* 不要为了 probe 清理未知 session。
* 失败要输出明确原因，例如权限不足、session 创建失败、provider enable 失败。

## 修改 SourceStatus

`MonitoringService.RefreshSourceStatuses` 中加入 ETW collector 的状态：

```csharp
TryAddStatus(() => _etwCollector.GetStatus(), statuses);
```

状态语义：

* `Available`：Process 和 TCP/IP provider 均启用，后台消费正常。
* `Partial`：至少一个 provider 可用，但部分 provider 或字段解析失败。
* `Requires admin`：权限不足导致无法启动 session 或启用关键 provider。
* `Unavailable`：ETW session 无法创建、后台消费失败、关键 provider 全部不可用。
* `Disabled`：仅用于未来显式配置关闭；第一版默认开启，一般不应出现，除非加入了关闭开关。

Details 应包含足够诊断信息，但不要刷屏或包含 raw event payload。

## 数据库存储

第一阶段不新增表。

原因：

* `process_samples` 已经有：

  * `network_receive_bytes_per_second`
  * `network_send_bytes_per_second`

* 直接填充现有字段即可。

不要保存 raw ETW event。

如果你认为必须新增表，先在代码注释和最终说明中说明原因，并保证 schema migration 不破坏旧数据库。但第一版原则上不应新增表。

## 自身性能打点

`PowerCulpritEventSource` 是有价值的后续增强，但不要和第一版 ETW 接入混在一起。

本阶段不要求实现：

```text
src/PowerCulprit.Core/Diagnostics/PowerCulpritEventSource.cs
```

后续可以考虑用于：

* MonitoringCycleStart
* MonitoringCycleStop
* CollectorStart
* CollectorStop
* DatabaseWriteStart
* DatabaseWriteStop
* EtwCollectorStart
* EtwCollectorStop
* EtwEventDropped 或 EtwError

要求仍然是：

* 不影响正常运行。
* 不引入复杂依赖。
* 不要用它替代 ILogger。
* 只是给 PerfView / WPA / dotnet-trace 分析使用。

## 测试要求

至少增加或修改这些测试：

1. `ProcessEtwMerger`：

   * ETW receive/send bytes 能正确换算成 bytes/s。
   * ETW 没有对应 PID 时不影响原样本。
   * ETW 中有对应 PID 但没有 `HasNetworkActivity` 时，不写 0，保持原网络字段。
   * interval <= 0 时不抛异常，且不合并网络速率。
   * 原 `CommandLine` / `ExecutablePath` / `ParentPid` 为空时可由 ETW 补齐。
   * 原字段已有值时不要被空 ETW 值覆盖。
   * 输入列表不被修改，返回新列表或原样本集合语义清晰。

2. `EtwProcessActivityAccumulator`：

   * ProcessStart 能更新 PID 元数据。
   * ProcessStop 不会立即删除 PID 元数据。
   * TCP send/receive 能正确按 PID 累加。
   * 无效 PID、无效 bytes 被忽略。
   * SnapshotAndReset 会清空周期计数，但保留元数据缓存。
   * PID reuse 时新的 ProcessStart 会替换旧 metadata。

3. `WindowsEtwActivityCollector`：

   * 如果难以在 CI/本机稳定跑真实 ETW session，不强制做真实集成测试。
   * 至少保证不可用/权限不足/session 创建失败/provider enable 失败路径不会抛出到上层。
   * 后台 task 异常会更新 SourceStatus。
   * `StopAsync` 在未启动、启动失败、重复调用情况下不抛异常。

4. `WindowsEtwActivityProbe`：

   * Probe 失败会返回状态，不抛到 CLI 顶层。
   * Probe 超时会快速返回并尝试清理。
   * 不强制真实 ETW provider 在 CI 中可用。

运行：

```powershell
dotnet build
dotnet test
```

如果由于本机环境缺少 Windows App SDK / .NET 10 SDK / ETW 权限导致不能完整运行，需要在最终说明中明确失败原因。

## 性能验证建议

第一版实现完成后，建议额外做本机 A/B 性能验证，再决定是否继续保持默认开启：

1. 当前版本，不启用 ETW。
2. 启用 ETW Process + TCP/IP。
3. 非管理员运行。
4. 管理员运行。
5. 浏览器视频播放 / 下载文件 / 空闲桌面 / 大量短进程场景。

观察：

* PowerCulprit 自身 CPU 平均和峰值。
* 内存增长。
* ETW event lost / buffer lost 情况。
* SourceStatus 是否稳定。
* 网络字段是否能写入 `process_samples`。
* 是否影响主采样周期。

如果默认开启带来明显额外开销，应后续增加 UI/CLI 开关或改为默认关闭。

## 验收标准

完成后请给出：

1. 修改了哪些文件。
2. 新增了哪些类。
3. ETW collector 支持哪些 provider / event。
4. ETW 默认开启时，如果权限不足、session 冲突或 provider 不可用，程序如何降级。
5. TCP 网络字段是否已经能写入 `process_samples`。
6. 未观察到网络事件的 PID 是否保持 null，而不是写 0。
7. `--diagnose` 中 ETW Probe 状态如何显示。
8. `SourceStatus` 中 ETW 状态如何显示。
9. `dotnet build` / `dotnet test` 结果。
10. 后续建议，例如：

    * UDP ETW
    * DiskIO ETW
    * CPU sample / context switch
    * exited short-lived process synthetic sample
    * ETW provider 可配置开关
    * 自身 `PowerCulpritEventSource` 性能打点

## 实现优先级

按这个顺序执行：

1. 实现 ETW 数据模型和 accumulator。
2. 实现 `ProcessEtwMerger` 和单元测试。
3. 加 TraceEvent 依赖，优先 pin 稳定版本。
4. 实现 `WindowsEtwActivityCollector`，先保证不可用时安全降级。
5. 实现 `WindowsEtwActivityProbe`，用于 `--diagnose` 短时检查。
6. 接入 `MonitoringService`，默认尝试启动 ETW collector。
7. 注册 Desktop / CLI DI。
8. 接入 diagnose / SourceStatus。
9. 跑 build/test。
10. 给出最终总结和性能验证建议。

不要一次性做过度复杂的 ETW GPU、Power、DiskIO、CPU sample。第一版以“默认尝试启用、稳定降级、TCP 网络字段补齐”为目标。
