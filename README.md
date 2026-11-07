# N_m3u8DL-RE（下载队列增强版）

基于 [nilaoda/N_m3u8DL-RE](https://github.com/nilaoda/N_m3u8DL-RE) 的二次开发分支。

上游能力（HLS / DASH / MSS 下载、多线程分片、AES-128 / CENC 解密、FFmpeg 混流、直播录制、工具命令等）全部保留，
本分支只做了一件事：**给命令行程序加一条下载队列**，解决"同时点十几个链接 → 十几个窗口并行下载 → 整机卡死"的问题。

- 默认**同时最多下载 2 个**，其余按点击顺序排队
- 每次入队、开始、结束都写进 **`下载队列.log`**，窗口被关掉也能从日志里找回链接
- 队列状态可查、可清空、可立即消费

---

## 一、下载队列

原来的 N_m3u8DL-RE 是"一次调用 = 一个进程 = 一组连接"。浏览器插件（如猫抓）连点几次，就有几个进程同时抢带宽。
现在每次调用都会先进入队列：

| 行为 | 说明 |
| --- | --- |
| 第 1、2 次调用 | 立刻开始下载（占用两个下载槽位） |
| 之后的调用 | 只把任务写进队列便退出，不占窗口、不占连接 |
| 有空位时 | 正在运行的工作进程自动取走下一个任务 |

相关参数：

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--queue` | `true` | 是否启用队列；`--queue false` 关闭（自己控制并发时用） |
| `--queue-concurrency <N>` | `2` | 同时下载的任务数，可填 1-8；填 1 就是完全串行 |
| `--queue-dir <DIR>` | 用户配置目录下 `N_m3u8DL-RE/queue` | 队列文件目录（Windows 为 `%APPDATA%\N_m3u8DL-RE\queue`） |
| `--queue-delay <秒>` | `1` | 同一个工作进程两个任务之间的冷却时间 |

几个实现上的取舍：

- **每个任务跑在独立子进程里**。单个任务崩溃、卡死、被杀都不会带走整条队列，任务之间也不会互相污染静态状态。
- **槽位用独占锁文件实现**（`worker.0.lock`、`worker.1.lock`…），进程异常退出时由操作系统释放句柄，不会出现"槽位被永久占住"。
- **崩溃恢复不抢别人的活**：正在执行的任务在 `running/` 里的文件名带 `@<pid>-<进程启动时间>`，只有确认那个进程真的没了，才会把任务放回队首重试。
- 队列任务保存的是**入队那一刻的完整命令行**，之后改配置文件不会影响已经排队的任务。

## 二、队列流水日志 `下载队列.log`

位置：exe 同目录的 `Logs\下载队列.log`（指定 `--log-file-path` 时写到它所在目录），首次创建带 UTF-8 BOM，记事本直接打开不乱码。

**入队那一刻就落盘**，所以哪怕窗口被强关、网页早就关了，链接和参数都还在：

```text
2026-11-05 17:21:51.188 [QUEUED] id=0639...823 name=X1 | CMD: D:\N_m3u8DL-RE\N_m3u8DL-RE.exe "https://example.com/a.m3u8" --save-dir "D:\下载" --save-name X1
2026-11-05 17:21:51.195 [START ] id=0639...823 name=X1 | slot=1/2
2026-11-05 17:21:51.595 [DONE  ] id=0639...823 name=X1 | exit=0 elapsed=00:00:12
2026-11-05 17:22:10.900 [RETRY ] id=0639...912 name=X2 | 上次未正常结束，已重新排队
```

| 事件 | 含义 |
| --- | --- |
| `QUEUED` | 已入队，附带完整可复制的命令 |
| `START` / `DONE` / `FAIL` | 开始 / 成功 / 失败（含退出码与耗时） |
| `RETRY` | 上次没跑完，启动时自动回到队首 |
| `ABORT` | 用户按 Ctrl+C 中断 |

用法：某个任务卡死 → 关掉窗口 → 打开日志，找到最后没有 `DONE` 的那条，把 `CMD:` 后面的命令复制出来重跑即可。

## 三、`queue` 管理命令

```text
N_m3u8DL-RE queue              # 列出待执行任务（含队列目录）
N_m3u8DL-RE queue --log        # 打印流水日志最后 60 行
N_m3u8DL-RE queue --clear      # 清空所有待执行 / 正在执行的任务
N_m3u8DL-RE queue --run        # 立刻开始消费队列（不动新任务）
```

---

## 四、快速开始

### 1. 直接用编译好的版本

到 [Releases](https://github.com/misakacoo/N_m3u8DL-RE/releases) 下载 `N_m3u8DL-RE.exe`（win-x64 单文件，自带运行时，无需安装 .NET）。

> 需要 **ffmpeg**：放到 PATH 里，或放在 exe 同目录，或 `--ffmpeg-binary-path` 指定。
> 合并、混流、读取媒体信息都依赖它。

### 2. 从源码构建

需要 .NET 10 SDK。

```bash
git clone https://github.com/misakacoo/N_m3u8DL-RE.git
cd N_m3u8DL-RE
dotnet build src/N_m3u8DL-RE.sln -c Release

# 单文件 AOT 发布（Windows 需要 C++ 生成工具；产物约 17 MB，无需运行时）
dotnet publish src/N_m3u8DL-RE/N_m3u8DL-RE.csproj -c Release -r win-x64 -o out

# 没有 C++ 工具链时，退回自包含单文件（体积大一些）
dotnet publish src/N_m3u8DL-RE/N_m3u8DL-RE.csproj -c Release -r win-x64 -o out \
  -p:PublishAot=false -p:PublishSingleFile=true --self-contained true
```

### 3. 配合浏览器插件使用

猫抓（Cat Catch）等插件的"调用外部程序"参数模板保持原样即可，例如：

```text
"${url}" --save-dir "D:\下载" --save-name "${title}" ${referer|exists:'--header "Referer:*"'} --thread-count 16 --auto-select
```

连点多个链接时：前两个立即下载，后面的静默排队，窗口一闪即关。

> 提示：并发 2 + `--thread-count 32` 就是 64 条连接。机器一般的话把 `--thread-count` 降到 16 更稳。

---

## 五、与上游的关系

- 上游仓库：<https://github.com/nilaoda/N_m3u8DL-RE>，本仓库保留其 MIT 许可证与全部原始功能。
- 本仓库**不包含**上游的单元测试工程，只保留可运行的程序本体（`N_m3u8DL-RE` / `.Common` / `.Parser` 三个项目）。
- 队列相关代码集中在 `src/N_m3u8DL-RE/Queue/`，命令行入口改动在 `CommandLine/CommandInvoker.cs`、`CommandLine/ToolCommands.cs`。
- 同步上游：把上游新版本合并进来后，重新按上面的命令构建即可；队列逻辑与下载管线本身是解耦的。

## 六、已知限制

- 直播录制会一直占用一个槽位，直到按 Ctrl+C 结束；需要同时录多路请调大 `--queue-concurrency` 或使用 `--queue false`。
- 队列按会话共享（同一台机器、同一个用户），不同用户各自一条队列。
- 队列流水日志只追加不轮转，长期使用可以自行删除。

## 七、许可证

与上游一致，采用 [MIT License](LICENSE)。
