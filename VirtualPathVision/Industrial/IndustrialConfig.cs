using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace VirtualPathVision.Industrial
{
    /// <summary>
    /// 工业互联配置模型（绑定 appsettings.json 的 Industrial 节）。
    /// </summary>
    public class IndustrialConfig
    {
        public ModbusConfig Modbus { get; set; } = new();
        public OpcUaConfig OpcUa { get; set; } = new();
        public SerialConfig Serial { get; set; } = new();
        public TcpScannerConfig TcpScanner { get; set; } = new();
        public ReportConfig Report { get; set; } = new();
    }

    /// <summary>Modbus TCP 配置</summary>
    public class ModbusConfig
    {
        public string Ip { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 502;
        public byte UnitId { get; set; } = 1;
    }

    /// <summary>OPC-UA 配置</summary>
    public class OpcUaConfig
    {
        public string Endpoint { get; set; } = "opc.tcp://127.0.0.1:4840";
    }

    /// <summary>串口扫码枪配置</summary>
    public class SerialConfig
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
    }

    /// <summary>TCP 扫码设备配置</summary>
    public class TcpScannerConfig
    {
        public int Port { get; set; } = 9001;
    }

    /// <summary>报工服务配置</summary>
    public class ReportConfig
    {
        public string DbPath { get; set; } = "workreport.db";
        public bool PlcReportEnable { get; set; } = false;
        public ushort PlcCountRegister { get; set; } = 100;
    }

    /// <summary>单条报工记录</summary>
    public class WorkReportRecord
    {
        public long Id { get; set; }
        public DateTime Time { get; set; }
        public string WorkOrder { get; set; } = "";
        public string Barcode { get; set; } = "";
        public string Source { get; set; } = "";

        /// <summary>列表显示文本</summary>
        public string DisplayText => $"[{Time:HH:mm:ss}] {WorkOrder} | {Barcode} | {Source}";
    }

    /// <summary>
    /// 报工服务：将扫码数据（条码/RFID）登记为生产报工记录。
    /// SQLite 持久化、今日产量统计、CSV 导出，并触发 PLC 联动事件。
    /// </summary>
    /// <remarks>
    /// 线程模型：
    /// <list type="bullet">
    /// <item>SQLite 访问串行化在 <c>_dbSync</c> 内，且每次操作使用短生命周期连接（SqliteConnection 非线程安全）。</item>
    /// <item>内存集合 <see cref="Records"/> 只能由调用方在 UI 线程改动 —— 它是 ObservableCollection，
    /// 跨线程 Add 会抛异常。本类的数据库操作可在任意线程调用，但结果集合的变更请在 UI 线程进行。</item>
    /// </list>
    /// </remarks>
    public class WorkReportService : IDisposable
    {
        private readonly object _dbSync = new();
        private readonly string _connectionString;
        private bool _disposed;

        /// <summary>报工记录集合（UI 绑定，仅 UI 线程访问）</summary>
        public ObservableCollection<WorkReportRecord> Records { get; } = new();

        /// <summary>新报工记录事件（供 PLC 联动等订阅）</summary>
        public event Action<WorkReportRecord>? OnRecordAdded;

        /// <summary>今日产量</summary>
        public int TodayCount { get; private set; }

        /// <summary>
        /// 可安全写入 PLC ushort 保持寄存器的今日产量：超出 ushort 范围时饱和到 65535，
        /// 避免调用方直接 (ushort)TodayCount 强转时静默回绕成 0。
        /// </summary>
        public ushort TodayCountForPlc => ClampToUshort(TodayCount);

        /// <summary>把计数夹到 ushort 范围（0..65535），超上限时饱和</summary>
        public static ushort ClampToUshort(int value)
        {
            if (value < 0)
                return 0;
            return value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;
        }

        public WorkReportService(IndustrialConfig config)
        {
            string dbPath = Path.Combine(AppContext.BaseDirectory, config.Report.DbPath);

            // 用 SqliteConnectionStringBuilder 生成连接串，避免路径中的空格/分号破坏解析
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,   // 短生命周期连接，不参与池化，避免文件句柄长期占用
            }.ToString();

            lock (_dbSync)
            {
                ThrowIfDisposed();

                using var connection = OpenConnection();
                Execute(connection, "PRAGMA journal_mode=WAL;");

                Execute(connection, @"CREATE TABLE IF NOT EXISTS work_reports (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    time TEXT NOT NULL,
                    work_order TEXT,
                    barcode TEXT NOT NULL,
                    source TEXT);");
            }

            LoadTodayRecords();
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>登记一条报工记录（UI 线程调用）</summary>
        public WorkReportRecord AddRecord(string workOrder, string barcode, string source)
        {
            var record = new WorkReportRecord
            {
                Time = DateTime.Now,
                WorkOrder = workOrder,
                Barcode = barcode,
                Source = source
            };

            lock (_dbSync)
            {
                ThrowIfDisposed();

                using var connection = OpenConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"INSERT INTO work_reports (time, work_order, barcode, source)
                                    VALUES ($time, $order, $barcode, $source);
                                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$time", record.Time.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("$order", record.WorkOrder);
                cmd.Parameters.AddWithValue("$barcode", record.Barcode);
                cmd.Parameters.AddWithValue("$source", record.Source);

                object? scalar = cmd.ExecuteScalar();
                if (scalar == null || scalar == DBNull.Value)
                {
                    throw new InvalidOperationException("写入报工记录后未能取得自增 Id");
                }
                record.Id = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }

            Records.Add(record);
            TodayCount++;
            OnRecordAdded?.Invoke(record);
            return record;
        }

        /// <summary>
        /// 导出全部记录为 CSV 文件，返回文件路径。
        /// 查询数据库全部历史记录（按 id 升序），而不是仅内存中的今日记录。
        /// </summary>
        /// <remarks>
        /// 该方法为同步阻塞（含文件写入），数据量大时会在调用线程上耗时；
        /// 当前调用点为 UI 线程，如记录量显著增长应改为在后台线程执行并回传 UI。
        /// </remarks>
        public string ExportCsv(string? path = null)
        {
            path ??= Path.Combine(
                AppContext.BaseDirectory,
                $"work_report_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var lines = new List<string> { "Time,WorkOrder,Barcode,Source" };

            lock (_dbSync)
            {
                ThrowIfDisposed();

                using var connection = OpenConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"SELECT time, work_order, barcode, source
                                    FROM work_reports ORDER BY id;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    lines.Add(string.Join(",",
                        Escape(reader.IsDBNull(0) ? "" : reader.GetString(0)),
                        Escape(reader.IsDBNull(1) ? "" : reader.GetString(1)),
                        Escape(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                        Escape(reader.IsDBNull(3) ? "" : reader.GetString(3))));
                }
            }

            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            return path;
        }

        /// <summary>清空今日报工记录（数据库 + 内存中的今日行）</summary>
        public void ClearToday()
        {
            DateTime today = DateTime.Today;

            lock (_dbSync)
            {
                ThrowIfDisposed();

                using var connection = OpenConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM work_reports WHERE date(time) = $today;";
                cmd.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }

            // 只移除今日行：Records.Clear() 会连带清掉内存中可能存在的历史行
            for (int i = Records.Count - 1; i >= 0; i--)
            {
                if (Records[i].Time.Date == today)
                    Records.RemoveAt(i);
            }
            TodayCount = 0;
        }

        private void LoadTodayRecords()
        {
            var loaded = new List<WorkReportRecord>();

            lock (_dbSync)
            {
                using var connection = OpenConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"SELECT id, time, work_order, barcode, source
                                    FROM work_reports WHERE date(time) = $today ORDER BY id;";
                cmd.Parameters.AddWithValue(
                    "$today", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    // 本方法在构造函数中调用（应用启动路径）：单行脏数据不得让启动失败，跳过即可
                    if (!TryParseStoredTime(reader.IsDBNull(1) ? null : reader.GetString(1), out DateTime time))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[WorkReportService] 跳过时间字段非法的记录 id={reader.GetValue(0)}");
                        continue;
                    }

                    loaded.Add(new WorkReportRecord
                    {
                        Id = reader.GetInt64(0),
                        Time = time,
                        WorkOrder = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Barcode = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Source = reader.IsDBNull(4) ? "" : reader.GetString(4)
                    });
                }
            }

            foreach (var record in loaded)
                Records.Add(record);
            TodayCount = Records.Count;
        }

        /// <summary>
        /// 解析库内时间文本。固定用 InvariantCulture，避免受当前区域设置影响；
        /// 解析失败返回 false（不抛异常），由调用方跳过该行。
        /// </summary>
        private static bool TryParseStoredTime(string? text, out DateTime time)
        {
            time = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (DateTime.TryParseExact(
                    text,
                    new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out time))
            {
                return true;
            }

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        }

        private static string Escape(string value)
        {
            // \r 必须一并处理：多数 CSV 解析器以 \r\n 为行分隔，只判 \n 会导致半行内容被拆错
            return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WorkReportService));
        }

        public void Dispose()
        {
            lock (_dbSync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
