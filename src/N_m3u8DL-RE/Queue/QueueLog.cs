using System.Text;

namespace N_m3u8DL_RE.Queue;

/// <summary>
/// 队列流水日志：把每个任务的入队、开始、结束（以及中断/重试）都追加到同一个可读文件里。
/// 终端窗口关掉、进程被杀之后，用户仍然能从日志里找回链接和完整命令行，直接复制重下。
/// 每次写入都独立打开文件，多个工作进程同时写不会互相覆盖。
/// </summary>
internal static class QueueLog
{
    internal const string FileName = "下载队列.log";

    private static readonly object Gate = new();
    private static string? _fallbackPath;

    // 首次创建时带 BOM，方便记事本等编辑器直接按 UTF-8 打开；追加时不会重复写入。
    private static readonly UTF8Encoding Utf8WithBom = new(true);

    /// <summary>
    /// 解析日志路径：优先 --log-file-path 所在目录，其次 exe 目录下的 Logs，够不着再退回用户目录。
    /// </summary>
    internal static string ResolvePath(string? logDirectory)
    {
        if (!string.IsNullOrWhiteSpace(logDirectory))
            return Path.Combine(logDirectory, FileName);
        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDirectory))
            return Path.Combine(exeDirectory, "Logs", FileName);
        return Path.Combine(Path.GetTempPath(), FileName);
    }

    internal static void Queued(QueuedJob job, string logDirectory)
    {
        Write(logDirectory, Format("QUEUED", job) + " | CMD: " + BuildCommand(job.Arguments));
    }

    internal static void Started(QueuedJob job, int slot, int slotCount, string logDirectory)
    {
        Write(logDirectory, Format("START ", job) + $" | slot={slot}/{slotCount}");
    }

    internal static void Finished(QueuedJob job, int exitCode, TimeSpan elapsed, string logDirectory)
    {
        var status = exitCode == 0 ? "DONE  " : "FAIL  ";
        Write(logDirectory,
            Format(status, job) + $" | exit={exitCode} elapsed={elapsed:hh\\:mm\\:ss}");
    }

    internal static void Restored(QueuedJob job, string logDirectory)
    {
        Write(logDirectory, Format("RETRY ", job) + " | 上次未正常结束，已重新排队");
    }

    internal static void Aborted(QueuedJob job, string logDirectory)
    {
        Write(logDirectory, Format("ABORT ", job) + " | 用户中断");
    }

    /// <summary>
    /// 读取日志末尾若干行，供 queue --log 查看最近的任务。
    /// </summary>
    internal static string[] Tail(string path, int lineCount)
    {
        if (!File.Exists(path) || lineCount <= 0)
            return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > lineCount)
                    lines.Dequeue();
            }

            return [.. lines];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 生成可以直接粘到命令行里重跑的命令行。
    /// </summary>
    internal static string BuildCommand(IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            builder.Append("N_m3u8DL-RE");
        }
        else if (QueueScheduler.IsDotnetHost(exe))
        {
            // dotnet 宿主启动时，命令行里还要带上程序集路径才能重跑。
            var assembly = Environment.GetCommandLineArgs().FirstOrDefault();
            builder.Append(Quote(exe));
            if (!string.IsNullOrEmpty(assembly) && Path.IsPathRooted(assembly))
                builder.Append(' ').Append(Quote(assembly));
        }
        else
        {
            builder.Append(Quote(exe));
        }

        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Format(string status, QueuedJob job) =>
        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{status}] id={job.Id} name={job.DisplayName ?? "-"}";

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// 追加一行。写不进去时换到用户目录再试一次，绝不因为日志失败影响下载。
    /// </summary>
    internal static void Write(string? logDirectory, string line)
    {
        lock (Gate)
        {
            var path = ResolvePath(logDirectory);
            if (TryAppend(path, line))
                return;
            if (_fallbackPath != null && TryAppend(_fallbackPath, line))
                return;

            var fallbackDirectory = DownloadQueue.GetDefaultDirectory();
            _fallbackPath = Path.Combine(Path.GetDirectoryName(fallbackDirectory) ?? fallbackDirectory, FileName);
            TryAppend(_fallbackPath, line);
        }
    }

    private static bool TryAppend(string path, string line)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8WithBom);
                writer.WriteLine(line);
                return true;
            }
            catch (IOException)
            {
                // 另一个进程正在写：短暂等待后重试。
                Thread.Sleep(20);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException)
            {
                return false;
            }
        }

        return false;
    }
}
