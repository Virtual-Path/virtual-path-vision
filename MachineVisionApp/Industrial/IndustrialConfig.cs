using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MachineVisionApp.Industrial
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
    /// 注意：AddRecord 必须在 UI 线程调用（内部更新绑定集合）。
    /// </summary>
    public class WorkReportService : IDisposable
    {
        private readonly SqliteConnection _connection;

        /// <summary>报工记录集合（UI 绑定，仅 UI 线程访问）</summary>
        public ObservableCollection<WorkReportRecord> Records { get; } = new();

        /// <summary>新报工记录事件（供 PLC 联动等订阅）</summary>
        public event Action<WorkReportRecord>? OnRecordAdded;

        /// <summary>今日产量</summary>
        public int TodayCount { get; private set; }

        public WorkReportService(IndustrialConfig config)
        {
            string dbPath = Path.Combine(AppContext.BaseDirectory, config.Report.DbPath);
            _connection = new SqliteConnection($"Data Source={dbPath}");
            _connection.Open();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS work_reports (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    time TEXT NOT NULL,
                    work_order TEXT,
                    barcode TEXT NOT NULL,
                    source TEXT);";
                cmd.ExecuteNonQuery();
            }

            LoadTodayRecords();
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

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO work_reports (time, work_order, barcode, source)
                                    VALUES ($time, $order, $barcode, $source);
                                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$time", record.Time.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("$order", record.WorkOrder);
                cmd.Parameters.AddWithValue("$barcode", record.Barcode);
                cmd.Parameters.AddWithValue("$source", record.Source);
                record.Id = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            Records.Add(record);
            TodayCount++;
            OnRecordAdded?.Invoke(record);
            return record;
        }

        /// <summary>导出全部记录为 CSV 文件，返回文件路径</summary>
        public string ExportCsv(string? path = null)
        {
            path ??= Path.Combine(
                AppContext.BaseDirectory,
                $"work_report_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var lines = new List<string> { "Time,WorkOrder,Barcode,Source" };
            foreach (var r in Records)
            {
                lines.Add($"{r.Time:yyyy-MM-dd HH:mm:ss},{Escape(r.WorkOrder)},{Escape(r.Barcode)},{Escape(r.Source)}");
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            return path;
        }

        /// <summary>清空今日报工记录（数据库 + 内存）</summary>
        public void ClearToday()
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM work_reports WHERE date(time) = $today;";
                cmd.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
                cmd.ExecuteNonQuery();
            }
            Records.Clear();
            TodayCount = 0;
        }

        private void LoadTodayRecords()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT id, time, work_order, barcode, source
                                FROM work_reports WHERE date(time) = $today ORDER BY id;";
            cmd.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Records.Add(new WorkReportRecord
                {
                    Id = reader.GetInt64(0),
                    Time = DateTime.Parse(reader.GetString(1)),
                    WorkOrder = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Barcode = reader.GetString(3),
                    Source = reader.IsDBNull(4) ? "" : reader.GetString(4)
                });
            }
            TodayCount = Records.Count;
        }

        private static string Escape(string value)
        {
            return value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        }

        public void Dispose()
        {
            try { _connection.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }
    }
}
