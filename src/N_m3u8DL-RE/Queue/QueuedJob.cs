using System.Text.Json.Serialization;

namespace N_m3u8DL_RE.Queue;

/// <summary>
/// 队列中的一个下载任务。保存最终生效的命令行参数和入队时的工作目录，
/// 让任务可以在另一个进程里原样重新执行。
/// </summary>
internal sealed class QueuedJob
{
    /// <summary>
    /// 任务标识，同时决定队列顺序：时间戳定宽，按文件名字典序即先进先出。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 入队时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 入队时的工作目录，用于还原命令行中的相对路径。
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 已与配置文件合并过的完整参数。
    /// </summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>
    /// 便于人阅读的任务名，仅用于日志。
    /// </summary>
    public string? DisplayName { get; set; }
}

// 队列文件使用源生成序列化：AOT 发布后不能依赖反射式 JsonSerializer。
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(QueuedJob))]
internal partial class QueueJsonContext : JsonSerializerContext
{
}
