using System.Diagnostics;
using N_m3u8DL_RE.Common.Resource;

namespace N_m3u8DL_RE.Queue;

/// <summary>
/// 队列参数。
/// </summary>
internal sealed class QueueSettings
{
    /// <summary>
    /// 队列根目录。
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>
    /// 两个任务之间的间隔；合并/混流刚结束时系统还在写盘，留一点时间能让下一任务跑得更稳。
    /// </summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 同时运行的下载数量，其余任务排队等待。
    /// </summary>
    public int Concurrency { get; init; } = 2;

    /// <summary>
    /// 队列流水日志所在目录；为空时与主日志一样放在 exe 目录的 Logs 下。
    /// </summary>
    public string? LogDirectory { get; init; }
}

/// <summary>
/// 队列调度器：把每一次下载调用变成队列里的一个任务，并用槽位把并发限制在 --queue-concurrency 之内（默认 2）。
/// 真正的下载放在子进程里执行，任务之间互不污染静态状态，单个任务崩溃也不会带走整条队列。
/// </summary>
internal static class QueueScheduler
{
    /// <summary>
    /// 子进程标记：带有这个环境变量的进程直接执行下载，不再进入队列。
    /// </summary>
    internal const string ChildEnvironmentVariable = "N_M3U8DL_RE_QUEUE_CHILD";

    /// <summary>
    /// 抢槽位等待时间；用来覆盖“上一个工作进程刚好释放槽位”的极短竞态窗口。
    /// </summary>
    private const int LockTimeoutMs = 800;

    private const int MaxDrainRounds = 10000;

    /// <summary>
    /// 当前进程是否是被队列调度起来的下载子进程。
    /// </summary>
    internal static bool IsQueueChild =>
        Environment.GetEnvironmentVariable(ChildEnvironmentVariable) == "1";

    /// <summary>
    /// 执行单个队列任务；测试可替换此委托以避免真的启动进程。
    /// </summary>
    internal static Func<QueuedJob, Task<int>> JobRunner { get; set; } = RunJobProcessAsync;

    /// <summary>
    /// 当前正在执行的任务；Ctrl+C 时用它记一条“用户中断”。
    /// </summary>
    private static QueuedJob? CurrentJob { get; set; }

    private static bool _cancelHooked;

    /// <summary>
    /// 注册一次 Ctrl+C 处理：窗口被用户强行关掉之前，至少把中断的任务写进流水日志。
    /// </summary>
    private static void HookCancel(QueueSettings settings)
    {
        if (_cancelHooked)
            return;
        _cancelHooked = true;
        Console.CancelKeyPress += (_, _) =>
        {
            if (CurrentJob is { } job)
                QueueLog.Aborted(job, settings.LogDirectory);
        };
    }

    /// <summary>
    /// 处理一次下载调用：先入队，抢到空闲槽位就消费队列，槽位全满就立即返回。
    /// </summary>
    public static async Task<int> SubmitAsync(QueueSettings settings, IReadOnlyList<string> arguments,
        string workingDirectory, string? displayName)
    {
        var queue = new DownloadQueue(settings.Directory);
        queue.EnsureCreated();
        var job = queue.Enqueue(arguments, workingDirectory, displayName);
        // 入队即落盘：即使随后进程被杀，也能从日志里找回链接和完整命令。
        QueueLog.Queued(job, settings.LogDirectory);
        var announced = false;

        for (var round = 0; round < MaxDrainRounds; round++)
        {
            if (!QueueSlotLock.TryAcquire(queue.Root, settings.Concurrency, LockTimeoutMs, out var slot))
            {
                // 槽位全满：本任务留在队列里，等正在跑的工作进程空出手来取走。
                if (!announced)
                {
                    var position = Math.Max(1, queue.CountPending());
                    Console.WriteLine("[Queue] " + string.Format(ResString.queueWaiting, position, Describe(job)));
                }

                return 0;
            }

            using (slot)
            {
                Console.WriteLine("[Queue] " + string.Format(ResString.queueSlotStarted, slot.Slot + 1,
                    settings.Concurrency));
                var exitCode = await DrainAsync(queue, settings, slot.Slot);

                // 释放槽位后再确认一次：刚好在这个间隙入队的任务不能被漏掉。
                if (!queue.HasPending())
                    return exitCode;
            }
        }

        return 0;
    }

    /// <summary>
    /// 不新增任务，直接消费队列中已排队的任务。
    /// </summary>
    public static async Task<int> RunPendingAsync(QueueSettings settings)
    {
        var queue = new DownloadQueue(settings.Directory);
        queue.EnsureCreated();
        if (!QueueSlotLock.TryAcquire(queue.Root, settings.Concurrency, 0, out var slot))
        {
            Console.WriteLine("[Queue] " + ResString.queueBusy);
            return 0;
        }

        using (slot)
            return await DrainAsync(queue, settings);
    }

    /// <summary>
    /// 顺序执行队列中的全部任务。调用者必须已经持有一个下载槽位。
    /// </summary>
    internal static async Task<int> DrainAsync(DownloadQueue queue, QueueSettings settings, int slotIndex = 0)
    {
        queue.EnsureCreated();
        // 上次被强杀时留在 running 里的任务放回队首，避免任务凭空消失。
        foreach (var restored in queue.RestoreRunning())
        {
            Console.WriteLine("[Queue] " + string.Format(ResString.queueRestored, Describe(restored)));
            QueueLog.Restored(restored, settings.LogDirectory);
        }

        HookCancel(settings);
        var executed = 0;
        var failed = 0;
        while (true)
        {
            if (!queue.TryDequeue(out var job, out var runningPath))
                break;

            executed++;
            var startedAt = DateTimeOffset.Now;
            Console.WriteLine("[Queue] " + string.Format(ResString.queueTaskStart, executed, Describe(job)));
            QueueLog.Started(job, slotIndex + 1, settings.Concurrency, settings.LogDirectory);
            CurrentJob = job;

            int exitCode;
            try
            {
                exitCode = await JobRunner(job);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Queue] {ResString.queueTaskCrash}: {ex.Message}");
                exitCode = -1;
            }

            CurrentJob = null;
            queue.Complete(runningPath);
            var elapsed = DateTimeOffset.Now - startedAt;
            QueueLog.Finished(job, exitCode, elapsed, settings.LogDirectory);
            if (exitCode == 0)
            {
                Console.WriteLine("[Queue] " + string.Format(ResString.queueTaskDone, Describe(job),
                    elapsed.ToString(@"hh\:mm\:ss")));
            }
            else
            {
                failed++;
                Console.Error.WriteLine("[Queue] " + string.Format(ResString.queueTaskFailed, Describe(job),
                    exitCode, elapsed.ToString(@"hh\:mm\:ss")));
            }

            if (settings.Delay > TimeSpan.Zero)
                await Task.Delay(settings.Delay);
        }

        Console.WriteLine("[Queue] " + string.Format(ResString.queueDrained, executed, failed));
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 在子进程里执行一个任务，输出直接复用当前控制台。
    /// </summary>
    private static async Task<int> RunJobProcessAsync(QueuedJob job)
    {
        using var process = new Process { StartInfo = CreateStartInfo(job) };
        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    /// <summary>
    /// 构造子进程启动参数：单文件发布直接启动自己，dotnet 宿主则补上程序集路径。
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(QueuedJob job)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Cannot resolve current process path.");
        var workingDirectory = !string.IsNullOrEmpty(job.WorkingDirectory) && Directory.Exists(job.WorkingDirectory)
            ? job.WorkingDirectory
            : Environment.CurrentDirectory;
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        if (IsDotnetHost(processPath))
        {
            // dotnet 宿主（dotnet app.dll）下入口是 dll 路径；单文件发布不会进入此分支。
            var entry = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrEmpty(entry) || !Path.IsPathRooted(entry) || !File.Exists(entry))
                throw new InvalidOperationException("Cannot resolve entry assembly path.");
            info.FileName = processPath;
            info.ArgumentList.Add(entry);
        }
        else
        {
            info.FileName = processPath;
        }

        foreach (var argument in job.Arguments)
            info.ArgumentList.Add(argument);

        info.Environment[ChildEnvironmentVariable] = "1";
        return info;
    }

    internal static bool IsDotnetHost(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);

    internal static string Describe(QueuedJob job) =>
        string.IsNullOrWhiteSpace(job.DisplayName) ? job.Id : job.DisplayName;
}
