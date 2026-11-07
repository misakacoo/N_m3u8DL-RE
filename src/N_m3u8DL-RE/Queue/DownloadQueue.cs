using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace N_m3u8DL_RE.Queue;

/// <summary>
/// 基于文件的下载队列。一个目录代表一条队列：
/// pending 存放待执行任务，running 存放正在执行的任务。
/// 入队和出队都只依赖文件系统的原子重命名，多个进程同时入队不会互相覆盖。
/// </summary>
internal sealed class DownloadQueue
{
    private const string PendingFolder = "pending";
    private const string RunningFolder = "running";
    private const string JobExtension = ".json";
    private const string TempPrefix = ".";

    // 允许的时间误差：同一进程的启动时间在不同进程里读取应当一致，留一点余量防止精度差异。
    private static readonly long OwnerTimeTolerance = TimeSpan.FromSeconds(5).Ticks;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    // 当前工作进程的身份，追加在 running 文件名后面：<任务Id>@<pid>-<启动时间>.json
    private static readonly string OwnerSuffix = "@" + BuildOwnerToken();

    public DownloadQueue(string directory)
    {
        Root = Path.GetFullPath(directory);
    }

    /// <summary>
    /// 队列根目录。
    /// </summary>
    public string Root { get; }

    private string PendingDir => Path.Combine(Root, PendingFolder);
    private string RunningDir => Path.Combine(Root, RunningFolder);

    /// <summary>
    /// 默认队列目录与 config.conf 位于同一个用户目录；没有可用的用户目录时退回临时目录。
    /// </summary>
    public static string GetDefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        var baseDir = Path.IsPathRooted(appData) ? appData : Path.GetTempPath();
        return Path.Combine(baseDir, "N_m3u8DL-RE", "queue");
    }

    internal static string ResolveDirectory(string? value) =>
        string.IsNullOrWhiteSpace(value) ? GetDefaultDirectory() : Path.GetFullPath(value);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(PendingDir);
        Directory.CreateDirectory(RunningDir);
    }

    /// <summary>
    /// 把任务写入待执行目录。先写到临时文件再原子改名，避免别的进程读到半个文件。
    /// </summary>
    public QueuedJob Enqueue(IReadOnlyList<string> arguments, string workingDirectory, string? displayName)
    {
        EnsureCreated();
        var now = DateTimeOffset.Now;
        var job = new QueuedJob
        {
            Id = $"{now.UtcDateTime.Ticks:D19}-{Guid.NewGuid():N}",
            CreatedAt = now,
            WorkingDirectory = workingDirectory,
            Arguments = [.. arguments],
            DisplayName = displayName
        };
        var temp = Path.Combine(PendingDir, TempPrefix + job.Id + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(job, QueueJsonContext.Default.QueuedJob), Utf8NoBom);
        File.Move(temp, Path.Combine(PendingDir, JobFileName(job.Id)), overwrite: true);
        return job;
    }

    /// <summary>
    /// 取出最早的一个任务并标记为正在执行；取不到时返回 false。
    /// </summary>
    public bool TryDequeue([NotNullWhen(true)] out QueuedJob? job, out string runningPath)
    {
        job = null;
        runningPath = string.Empty;
        EnsureCreated();
        foreach (var candidate in Directory.EnumerateFiles(PendingDir, "*" + JobExtension).Order(StringComparer.Ordinal))
        {
            // 目标文件名带上持有者信息，其它工作进程据此判断这个任务是不是“活人”在跑。
            var name = StripOwner(Path.GetFileNameWithoutExtension(candidate));
            var target = Path.Combine(RunningDir, name + OwnerSuffix + JobExtension);
            try
            {
                // 目标已存在说明上一个进程留下的同名文件，直接覆盖即可。
                File.Move(candidate, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 另一个进程已经取走，换下一个。
                continue;
            }

            var loaded = TryRead(target);
            if (loaded == null)
            {
                // 文件损坏时删除，避免整条队列卡在同一个坏任务上。
                TryDelete(target);
                continue;
            }

            job = loaded;
            runningPath = target;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 任务执行完成（无论成功失败），从队列中移除。
    /// </summary>
    public void Complete(string runningPath) => TryDelete(runningPath);

    /// <summary>
    /// 把异常退出（或旧版本遗留）的任务放回队首，避免任务丢失。
    /// 仍在运行的工作进程持有的任务会被跳过，因此多个槽位可以同时执行。
    /// </summary>
    public List<QueuedJob> RestoreRunning()
    {
        EnsureCreated();
        var restored = new List<QueuedJob>();
        foreach (var file in Directory.EnumerateFiles(RunningDir, "*" + JobExtension))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (IsOwnerRunning(name))
                continue;
            var target = Path.Combine(PendingDir, StripOwner(name) + JobExtension);
            try
            {
                // 别的槽位可能刚刚处理掉同一个文件，移动前重新确认一次持有者状态。
                if (IsOwnerRunning(name))
                    continue;
                File.Move(file, target, overwrite: true);
                if (TryRead(target) is { } job)
                    restored.Add(job);
            }
            catch (FileNotFoundException)
            {
                // 另一个工作进程已经把它移走了，正常竞争。
            }
            catch (DirectoryNotFoundException)
            {
                ;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[Queue] restore failed: {file} ({ex.Message})");
            }
        }

        return restored;
    }

    /// <summary>
    /// 按先进先出顺序列出待执行任务，不改变队列状态。
    /// </summary>
    public List<QueuedJob> Peek()
    {
        EnsureCreated();
        var jobs = new List<QueuedJob>();
        foreach (var file in Directory.EnumerateFiles(PendingDir, "*" + JobExtension).Order(StringComparer.Ordinal))
        {
            var job = TryRead(file);
            if (job != null)
                jobs.Add(job);
        }

        return jobs;
    }

    /// <summary>
    /// 清理全部待执行和正在执行的任务，返回清理数量。
    /// </summary>
    public int Clear()
    {
        EnsureCreated();
        var count = 0;
        foreach (var dir in new[] { PendingDir, RunningDir })
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (TryDelete(file))
                    count++;
            }
        }

        // 顺手清掉旧版本（单槽位）留下的锁文件。
        TryDelete(Path.Combine(Root, "queue.lock"));
        return count;
    }

    /// <summary>
    /// 待执行任务数量。
    /// </summary>
    public int CountPending() =>
        Directory.Exists(PendingDir) ? CountJobs(PendingDir) : 0;

    public bool HasPending() => CountPending() > 0;

    private static int CountJobs(string directory) =>
        Directory.EnumerateFiles(directory, "*" + JobExtension)
            .Count(file => !Path.GetFileName(file).StartsWith(TempPrefix, StringComparison.Ordinal));

    /// <summary>
    /// 去掉文件名里的持有者后缀，任务 Id 本身就是完整前缀。
    /// </summary>
    private static string StripOwner(string name)
    {
        var index = name.IndexOf('@');
        return index < 0 ? name : name[..index];
    }

    /// <summary>
    /// 当前进程的 pid 与启动时间，用于识别 running 里的任务是否还有人执行。
    /// </summary>
    private static string BuildOwnerToken()
    {
        long ticks = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            ticks = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            ;
        }

        return $"{Environment.ProcessId}-{ticks}";
    }

    /// <summary>
    /// 判断 running 文件对应的持有进程是否还活着。多槽位并行时，别的槽位正在跑的任务不能被回收。
    /// </summary>
    private static bool IsOwnerRunning(string fileName)
    {
        var at = fileName.IndexOf('@');
        if (at < 0)
            return false; // 旧版本遗留的任务没有持有者信息，按残留处理
        var owner = fileName[(at + 1)..];
        var dash = owner.IndexOf('-');
        if (dash < 0 || !int.TryParse(owner[..dash], out var pid) ||
            !long.TryParse(owner[(dash + 1)..], out var ticks) || ticks == 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return Math.Abs(process.StartTime.ToUniversalTime().Ticks - ticks) <= OwnerTimeTolerance;
        }
        catch (ArgumentException)
        {
            return false; // 进程不存在
        }
        catch (InvalidOperationException)
        {
            return false; // 进程已退出
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // 无法判断时保守处理：认为还在执行，避免同一任务被两个进程同时跑。
            return true;
        }
    }

    private static string JobFileName(string id) => id + JobExtension;

    private static QueuedJob? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Utf8NoBom);
            var job = JsonSerializer.Deserialize(json, QueueJsonContext.Default.QueuedJob);
            if (job == null || job.Arguments.Length == 0)
            {
                Console.Error.WriteLine($"[Queue] invalid job file skipped: {path}");
                return null;
            }

            return job;
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine($"[Queue] cannot read job file: {path} ({ex.Message})");
            return null;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
