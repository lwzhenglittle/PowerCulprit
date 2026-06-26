# PowerCulprit

PowerCulprit 是一个面向 Windows 的电池耗电分析工具。它会持续采样整机电池状态、硬件传感器、进程 CPU、内存、磁盘、网络、前台窗口状态以及可用的 GPU 活动数据，把数据写入本地 SQLite 数据库，并根据进程活动与整机放电曲线之间的相关性生成耗电嫌疑排行。

请注意：PowerCulprit 是一个“相关性与归因”工具，不是每进程真实功耗计量器。它不会承诺某个进程真实消耗了多少瓦，而是帮助你判断最近一段时间哪些进程、硬件活动或前后台行为最可能与电池加速下降有关。

## 项目作用

PowerCulprit 主要用于回答：

> 最近一段时间，哪些进程和哪些硬件活动最像是在推高耗电？

它可以帮助你：

- 观察电池容量、电量变化和放电趋势。
- 采样每个进程的 CPU、内存、线程、句柄、磁盘读写、网络活动等指标。
- 结合硬件传感器读取 CPU 包功耗、Intel 核显或 Intel Arc 相关活动。
- 通过 Windows GPU Engine 数据估计进程级 GPU 活动。
- 通过 Windows ETW 增量采集补充进程生命周期和 TCP 网络活动。
- 将采样数据持久化到 SQLite，便于后续查看历史窗口和排行。
- 在图形界面中查看实时监控面板、状态、曲线和进程排行。
- 在无界面模式下采样一段时间，用于诊断或长期记录。

当前项目定位和限制：

- 目标系统：Windows 10 / Windows 11。
- 技术栈：C#、.NET 10、WinUI 3、Windows App SDK、SQLite。
- 硬件方向：Intel CPU 与 Intel 核显 / Intel Arc。
- 不支持 NVIDIA、AMD、外接独显专用采集路径。
- 缺失的数据会保持为空，不会编造估算值。
- 部分硬件传感器、ETW 或性能计数器可能需要管理员权限；权限不足时程序应降级运行，并在诊断输出或状态面板中说明原因。

## 仓库结构

```text
src/PowerCulprit.Core        核心模型、分析器、相关性计算和服务接口
src/PowerCulprit.Storage     SQLite 数据库、表结构、写入和查询逻辑
src/PowerCulprit.Collectors  Windows 数据采集器和监控主循环
src/PowerCulprit.Desktop     WinUI 3 图形界面和托盘入口
src/PowerCulprit.Cli         命令行入口、诊断模式和无界面采样模式
tests/PowerCulprit.Tests     单元测试和采集降级行为测试
```

## 环境要求

- Windows 10 或 Windows 11。
- .NET SDK 10.0.301。仓库通过 `global.json` 固定 SDK 版本。
- 能够还原 NuGet 包的网络环境。
- 如需读取部分硬件传感器、ETW 或性能计数器，建议以管理员身份运行；非管理员也可以运行主功能，但部分数据源可能显示不可用或需要管理员权限。

## 编译方式

在仓库根目录运行：

```powershell
dotnet build
```

该命令会构建整个解决方案，包括核心库、存储层、采集器、命令行程序、桌面程序和测试项目。

如果只想构建命令行入口：

```powershell
dotnet build src\PowerCulprit.Cli\PowerCulprit.Cli.csproj
```

如果只想构建桌面程序：

```powershell
dotnet build src\PowerCulprit.Desktop\PowerCulprit.Desktop.csproj
```

运行测试：

```powershell
dotnet test
```

发布桌面程序示例：

```powershell
dotnet publish src\PowerCulprit.Desktop\PowerCulprit.Desktop.csproj -c Release -r win-x64
```

## 用法

### 启动图形界面

默认运行命令行项目会启动桌面图形界面：

```powershell
dotnet run --project src\PowerCulprit.Cli
```

也可以直接运行桌面项目：

```powershell
dotnet run --project src\PowerCulprit.Desktop
```

图形界面用于查看实时采样、数据源状态、电池曲线、进程活动和耗电嫌疑排行。关闭窗口时程序会隐藏到托盘；只有从托盘菜单退出才会结束程序。

### 无界面采样

无界面模式适合在后台采样一段时间，不启动桌面界面：

```powershell
dotnet run --project src\PowerCulprit.Cli -- --headless --duration 30s
```

采样 30 分钟，采样间隔 2 秒，并写入指定数据库：

```powershell
dotnet run --project src\PowerCulprit.Cli -- --headless --duration 30m --interval 2 --db C:\temp\powerculprit.db
```

启用 Windows GPU Engine 进程级 GPU 采样：

```powershell
dotnet run --project src\PowerCulprit.Cli -- --headless --duration 10m --enable-gpu
```

说明：GPU Engine 采样开销较高，因此不是默认开启。只有需要分析进程级 GPU 活动时再使用。

### 诊断数据源状态

诊断当前系统上各采集数据源是否可用：

```powershell
dotnet run --project src\PowerCulprit.Cli -- --diagnose
```

诊断输出会显示电池接口、进程采样、GPU Engine、硬件传感器、Intel CPU / iGPU 派生功耗、Windows ETW 等数据源状态，并说明是否需要管理员权限。

### 常用参数

```text
--headless       使用无界面采样模式
--diagnose       输出数据源和权限诊断信息
--duration       采样时长，支持 ms、s、m、h 后缀；裸数字按秒处理
--interval       采样间隔，单位为秒，当前会限制在 1 到 5 秒之间
--db             指定 SQLite 数据库路径
--enable-gpu     启用 Windows GPU Engine 进程级 GPU 采样
--gpu            --enable-gpu 的简写
```

默认数据库路径：

```text
%LocalAppData%\PowerCulprit\powerculprit.db
```

默认日志目录：

```text
%LocalAppData%\PowerCulprit\logs
```

## 数据解释

PowerCulprit 的排行分数来自多个信号的组合，包括：

- 进程 CPU 活动。
- 进程磁盘读写。
- 进程网络收发。
- 进程 GPU Engine 活动。
- 进程是否位于前台。
- 系统电池容量下降曲线。
- 可用的 CPU / iGPU 硬件功耗或利用率信号。
- 进程启动、停止和短生命周期进程活动。

排行中的解释文本会说明某个进程为什么被排在前面，也会指出关键输入是否缺失。例如：没有可用的电池放电相关性、没有硬件传感器、没有观测到网络事件等。

网络字段来自 ETW TCP/IP 事件的进程级活动聚合，只表示采样窗口内观察到的 TCP 收发活动；未观察到网络事件的进程会保持为空，而不是写成 0。

## 注意事项

- 本项目不是每进程瓦特计，不应把排行分数解释为真实功率。
- 不同电脑、不同权限、不同驱动和不同传感器可用性会影响采集结果。
- 非管理员运行时，部分 ETW、硬件传感器或性能计数器可能不可用。
- GPU Engine 采样可能带来较高额外开销，需要时再开启。
- 采样数据保存在本地 SQLite 数据库中，请根据需要清理或备份。
