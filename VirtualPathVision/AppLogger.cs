using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VirtualPathVision
{
    /// <summary>
    /// 应用级日志服务（单例模式）。
    /// 提供带时间戳的日志记录功能，日志集合可绑定到 UI。
    /// </summary>
    public class AppLogger
    {
        private static readonly AppLogger _instance = new();
        public static AppLogger Instance => _instance;

        /// <summary>日志条目集合（可 UI 绑定）</summary>
        public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        /// <summary>新日志事件</summary>
        public event Action<LogEntry>? OnLogAdded;

        /// <summary>记录一条信息日志</summary>
        public void Info(string message)
        {
            AddEntry("INFO", message);
        }

        /// <summary>记录一条警告日志</summary>
        public void Warn(string message)
        {
            AddEntry("WARN", message);
        }

        /// <summary>记录一条错误日志</summary>
        public void Error(string message)
        {
            AddEntry("ERROR", message);
        }

        private void AddEntry(string level, string message)
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Message = message
            };

            // UI 线程调度交由调用方处理
            Entries.Add(entry);
            OnLogAdded?.Invoke(entry);
        }

        /// <summary>清空所有日志</summary>
        public void Clear()
        {
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
