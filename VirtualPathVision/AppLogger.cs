using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace VirtualPathVision
{
    /// <summary>
    /// 应用级日志服务（单例模式）。
    /// 提供带时间戳的日志记录功能，日志集合可绑定到 UI。
    ///
    /// 线程安全：Entries 是 ObservableCollection，被 ListBox 绑定，
    /// 只能由 UI 线程修改。因此 AddEntry 会把写入封送到 UI 调度器；
    /// OnLogAdded 同样在 UI 线程触发，订阅者无需再自行 Invoke。
    /// 为避免拖动滑块等高频操作刷屏，集合设置了容量上限，超出后丢弃最旧条目。
    /// </summary>
    public class AppLogger
    {
        private static readonly AppLogger _instance = new();
        public static AppLogger Instance => _instance;

        /// <summary>日志集合容量上限（防止长时间运行内存无限增长）</summary>
        public const int MaxEntries = 2000;

        private readonly object _pendingLock = new();
        private readonly System.Collections.Generic.Queue<LogEntry> _pending = new();

        /// <summary>日志条目集合（可 UI 绑定，仅 UI 线程读写）</summary>
        public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        /// <summary>新日志事件（在 UI 线程触发）</summary>
        public event Action<LogEntry>? OnLogAdded;

        /// <summary>记录一条信息日志</summary>
        public void Info(string message) => AddEntry("INFO", message);

        /// <summary>记录一条警告日志</summary>
        public void Warn(string message) => AddEntry("WARN", message);

        /// <summary>记录一条错误日志</summary>
        public void Error(string message) => AddEntry("ERROR", message);

        private void AddEntry(string level, string message)
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Message = message ?? ""
            };

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                AppendOnUiThread(entry);
                return;
            }

            // 来自后台线程：先排队，稍后由 FlushPending 批量写入 UI 线程
            bool flushNow;
            lock (_pendingLock)
            {
                if (_pending.Count >= MaxEntries)
                    _pending.Dequeue();
                _pending.Enqueue(entry);
                flushNow = _pending.Count >= 64;
            }

            if (flushNow && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(FlushPending);
        }

        /// <summary>把后台线程排队的日志批量写入 UI 集合</summary>
        public void FlushPending()
        {
            if (Application.Current?.Dispatcher.CheckAccess() != true)
            {
                var d = Application.Current?.Dispatcher;
                if (d != null && !d.HasShutdownStarted)
                    d.BeginInvoke(FlushPending);
                return;
            }

            System.Collections.Generic.Queue<LogEntry> batch;
            lock (_pendingLock)
            {
                if (_pending.Count == 0) return;
                batch = new System.Collections.Generic.Queue<LogEntry>(_pending);
                _pending.Clear();
            }

            foreach (var entry in batch)
                AppendOnUiThread(entry);
        }

        /// <summary>在 UI 线程追加一条日志并裁剪到容量上限（必须在 UI 线程调用）</summary>
        private void AppendOnUiThread(LogEntry entry)
        {
            while (Entries.Count >= MaxEntries)
                Entries.RemoveAt(0);

            Entries.Add(entry);
            OnLogAdded?.Invoke(entry);
        }

        /// <summary>清空所有日志</summary>
        public void Clear()
        {
            if (Application.Current?.Dispatcher.CheckAccess() != true)
            {
                var d = Application.Current?.Dispatcher;
                if (d != null && !d.HasShutdownStarted)
                    d.BeginInvoke(Clear);
                return;
            }

            lock (_pendingLock) _pending.Clear();
            Entries.Clear();
        }
    }

    /// <summary>单条日志记录</summary>
    public class LogEntry
    {
        public DateTime Time { get; set; }
        public string Level { get; set; } = "";
        public string Message { get; set; } = "";

        /// <summary>格式化的显示文本（供 UI 绑定）</summary>
        public string DisplayText => $"[{Time:HH:mm:ss}] [{Level}] {Message}";
    }
}