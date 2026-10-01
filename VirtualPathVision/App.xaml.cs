using System;
using System.Globalization;
using System.Windows;
using VirtualPathVision.Industrial;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SQLitePCL;

namespace VirtualPathVision
{
    /// <summary>
    /// 应用程序入口点。
    /// 负责应用级资源加载、依赖注入容器构建和启动配置。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>全局依赖注入容器（企业化架构核心）</summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>
        /// 启动配置：默认语言英语 + 构建 DI 容器（配置、驱动、服务）。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局异常兜底：弹出可读的错误对话框，避免静默崩溃
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // SQLitePCL provider initialization (必须在任何 SQLite 操作之前)
            // Microsoft.Data.Sqlite 8.0 使用 SQLitePCLRaw.bundle_e_sqlite3，显式注册 provider
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());

            // 加载保存的语言偏好，默认英语
            string savedCulture = TranslationService.Instance.LoadLanguage();
            var culture = new CultureInfo(savedCulture);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;

            // 1. 配置系统：appsettings.json（工业互联参数、可热重载）
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();

            // 2. 依赖注入容器：协议驱动、扫码源、报工服务
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(configuration
                .GetSection("Industrial")
                .Get<IndustrialConfig>() ?? new IndustrialConfig());
            services.AddSingleton(sp =>
            {
                var cfg = sp.GetRequiredService<IndustrialConfig>();
                return new ModbusTcpDriver(cfg.Modbus.Ip, cfg.Modbus.Port, cfg.Modbus.UnitId);
            });
            services.AddSingleton(sp =>
            {
                var cfg = sp.GetRequiredService<IndustrialConfig>();
                return new OpcUaDriver(cfg.OpcUa.Endpoint);
            });
            services.AddSingleton(sp =>
            {
                var cfg = sp.GetRequiredService<IndustrialConfig>();
                return new SerialScanDriver
                {
                    PortName = cfg.Serial.PortName,
                    BaudRate = cfg.Serial.BaudRate
                };
            });
            services.AddSingleton(sp =>
            {
                var cfg = sp.GetRequiredService<IndustrialConfig>();
                return new TcpScanDriver { Port = cfg.TcpScanner.Port };
            });
            services.AddSingleton<WorkReportService>();

            Services = services.BuildServiceProvider();

            // 初始化主题服务
            ThemeService.Instance.Initialize();

            base.OnStartup(e);
        }

        /// <summary>退出时释放容器中的可释放服务</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (Services is IDisposable disposable)
                    disposable.Dispose();
            }
            catch { }
            base.OnExit(e);
        }

        /// <summary>UI 线程未处理异常：显示错误信息（便于排查），启动阶段失败则退出。</summary>
        private void OnDispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            ShowCrashDialog(e.Exception);
            e.Handled = true;

            // 主窗口尚未建立时（启动失败），退出而不是滞留空进程
            if (MainWindow == null || !MainWindow.IsLoaded)
                Shutdown(-1);
        }

        /// <summary>非 UI 线程未处理异常：显示错误信息。</summary>
        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ShowCrashDialog(e.ExceptionObject as Exception);
        }

        private static void ShowCrashDialog(Exception? ex)
        {
            try
            {
                MessageBox.Show(
                    ex?.ToString() ?? "Unknown error",
                    "Virtual Path Vision — Unhandled error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
    }
}
