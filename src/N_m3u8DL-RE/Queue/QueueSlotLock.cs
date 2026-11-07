using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace N_m3u8DL_RE.Queue;

/// <summary>
/// 下载槽位锁：每个槽位是一个独占打开的锁文件，用于把同时运行的下载数量限制在 N 个。
/// 进程崩溃时由操作系统关闭句柄自动释放，不会出现“槽位被永久占住”。
/// </summary>
internal sealed class QueueSlotLock : IDisposable
{
    private const string LockPrefix = "worker.";
    private const string LockSuffix = ".lock";

    private readonly FileStream _stream;
    private bool _disposed;

    private QueueSlotLock(int slot, string lockPath, FileStream stream)
    {
        Slot = slot;
        LockPath = lockPath;
        _stream = stream;
    }

    /// <summary>
    /// 槽位序号，从 0 开始。
    /// </summary>
    public int Slot { get; }

    public string LockPath { get; }

    /// <summary>
    /// 尝试占用任意一个空闲槽位。timeoutMs 用来覆盖“槽位刚刚释放”的竞态窗口；0 表示只尝试一次。
    /// </summary>
    public static bool TryAcquire(string queueRoot, int slotCount, int timeoutMs,
        [NotNullWhen(true)] out QueueSlotLock? handle)
    {
        handle = null;
        var count = Math.Max(1, slotCount);
        Directory.CreateDirectory(queueRoot);
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            for (var slot = 0; slot < count; slot++)
            {
                var path = Path.Combine(queueRoot, $"{LockPrefix}{slot}{LockSuffix}");
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                        1, FileOptions.None);
                    handle = new QueueSlotLock(slot, path, stream);
                    return true;
                }
                catch (IOException)
                {
                    // 该槽位已被占用，继续看下一个。
                }
                catch (UnauthorizedAccessException ex)
                {
                    // 目录只读等异常情况：退化为单进程执行，不阻塞用户。
                    Console.Error.WriteLine($"[Queue] slot lock unavailable: {ex.Message}");
                    return false;
                }
            }

            if (Environment.TickCount64 >= deadline)
                return false;
            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Dispose();
    }
}
