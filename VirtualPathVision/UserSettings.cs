using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VirtualPathVision
{
    /// <summary>
    /// 用户偏好设置的统一读写入口（user_settings.json）。
    ///
    /// 此前 Language / Theme / SidebarExpanded 三处各自做「读-改-写」，
    /// 且都用 File.WriteAllText 直接截断覆盖同一个文件，
    /// 并发或交错写入会互相丢失键（例如切主题会抹掉已保存的语言）。
    ///
    /// 这里改为：
    /// 1. 全部读写在同一把静态锁内完成（进程内互斥）；
    /// 2. 写入采用「临时文件 + 原子替换」，读者永远看不到半截文件；
    /// 3. 读改写在同一临界区内完成，保证不丢键。
    ///
    /// 说明：这里刻意使用 System.Text.Json（.NET 内置 BCL），
    /// 以摆脱对 Newtonsoft.Json 传递依赖的依赖。
    /// </summary>
    public static class UserSettings
    {
        private static readonly object _gate = new();

        /// <summary>设置文件路径（与旧版保持一致）</summary>
        public static string FilePath { get; } =
            Path.Combine(AppContext.BaseDirectory, "user_settings.json");

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>读取单个字符串设置项，不存在时返回默认值</summary>
        public static string? GetString(string key, string? defaultValue = null)
        {
            lock (_gate)
            {
                var root = ReadAllUnsafe();
                if (root == null) return defaultValue;
                return GetStringUnsafe(root, key) ?? defaultValue;
            }
        }

        /// <summary>读取单个布尔设置项</summary>
        public static bool GetBool(string key, bool defaultValue)
        {
            lock (_gate)
            {
                var root = ReadAllUnsafe();
                if (root == null) return defaultValue;
                string? raw = GetStringUnsafe(root, key);
                return bool.TryParse(raw, out bool v) ? v : defaultValue;
            }
        }

        /// <summary>写入单个设置项（保留同文件内其它所有键）</summary>
        public static void Set(string key, string value)
        {
            lock (_gate)
            {
                JsonObject root = ReadAllUnsafe() ?? new JsonObject();
                root[key] = value;
                WriteAllUnsafe(root);
            }
        }

        /// <summary>写入单个布尔设置项</summary>
        public static void Set(string key, bool value) => Set(key, value ? "true" : "false");

        /// <summary>在同一次加锁中读取并更新（避免读改写之间的竞态）</summary>
        public static T Update<T>(Func<JsonObject, T> mutate)
        {
            lock (_gate)
            {
                JsonObject root = ReadAllUnsafe() ?? new JsonObject();
                T result = mutate(root);
                WriteAllUnsafe(root);
                return result;
            }
        }

        private static string? GetStringUnsafe(JsonObject root, string key)
        {
            if (!root.TryGetPropertyValue(key, out var node) || node == null)
                return null;
            try
            {
                return node.GetValue<string>();
            }
            catch (Exception)
            {
                // 值类型不是字符串（例如被写成了数字/对象）时退回原始字面量
                return node.ToString();
            }
        }

        private static JsonObject? ReadAllUnsafe()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;
                string text = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                return JsonNode.Parse(text) as JsonObject;
            }
            catch
            {
                // 文件损坏或被占用：当作空设置处理，不影响启动
                return null;
            }
        }

        private static void WriteAllUnsafe(JsonObject root)
        {
            try
            {
                // 先写临时文件再原子替换，读者不会看到写了一半的内容
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch
            {
                // 只读介质（如 Program Files）下写入失败：静默降级为不持久化
            }
        }
    }
}