using System.Collections.Concurrent;
using System.Text;

namespace SoundPipe.Core.Logging;

/// <summary>
/// 轻量滚动文件日志：%APPDATA%\SoundPipe\logs\soundpipe.log，
/// 单文件超过 1MB 时滚动为 soundpipe.1.log ~ soundpipe.5.log（共 5 个，最旧删除）。
/// 日志只包含诊断信息，不含任何音频数据（设计 §1 安全声明、F14）。
/// </summary>
public static class Log
{
    private const long MaxFileBytes = 1_000_000; // 1MB
    private const int MaxRollFiles = 5;

    private static readonly BlockingCollection<string> Queue = new(boundedCapacity: 4096);
    private static readonly Thread WriterThread;
    private static string _dir = null!;
    private static string _filePath = null!;

    /// <summary>日志目录（UI 提示用）</summary>
    public static string LogFolder => _dir;

    static Log()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoundPipe", "logs");
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "soundpipe.log");

        WriterThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "SoundPipe.Log"
        };
        WriterThread.Start();
    }

    public static void Info(string message) => Enqueue("INF", message);
    public static void Warn(string message) => Enqueue("WRN", message);
    public static void Error(string message) => Enqueue("ERR", message);
    public static void Error(string message, Exception ex) =>
        Enqueue("ERR", $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Enqueue(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        // 满时丢弃而非阻塞调用方（日志不应影响音频线程）
        Queue.TryAdd(line);
    }

    private static void WriteLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // 磁盘/占用问题静默重试下一行
            }
            catch (Exception)
            {
                // 防御：日志线程永不退出
            }
        }
    }

    private static void RotateIfNeeded()
    {
        var fi = new FileInfo(_filePath);
        if (!fi.Exists || fi.Length < MaxFileBytes) return;

        // soundpipe.4.log -> soundpipe.5.log ... soundpipe.log -> soundpipe.1.log
        var oldest = Path.Combine(_dir, $"soundpipe.{MaxRollFiles}.log");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = MaxRollFiles - 1; i >= 1; i--)
        {
            var src = Path.Combine(_dir, $"soundpipe.{i}.log");
            var dst = Path.Combine(_dir, $"soundpipe.{i + 1}.log");
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
        File.Move(_filePath, Path.Combine(_dir, "soundpipe.1.log"), overwrite: true);
    }
}
