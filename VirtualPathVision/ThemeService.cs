using System;
using System.IO;
using System.Linq;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VirtualPathVision
{
    public enum AppTheme
    {
        Light,
        Dark,
        System
    }

    public class ThemeService
    {
        private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
        public static ThemeService Instance => _instance.Value;

        public event EventHandler<AppTheme>? ThemeChanged;

        private AppTheme _currentTheme = AppTheme.Dark;
        public AppTheme CurrentTheme
        {
            get => _currentTheme;
            private set
            {
                if (_currentTheme != value)
                {
                    _currentTheme = value;
                    ThemeChanged?.Invoke(this, value);
                }
            }
        }

        private ThemeService() { }

        public void Initialize()
        {
            // 从 user_settings.json 加载主题偏好
            var savedTheme = LoadThemePreference();
            _currentTheme = savedTheme; // 同步字段，避免设置窗口误判为默认 Dark
            ApplyTheme(savedTheme);
        }

        public void SetTheme(AppTheme theme)
        {
            CurrentTheme = theme;
            ApplyTheme(theme);
            SaveThemePreference(theme);
        }

        private void ApplyTheme(AppTheme theme)
        {
            var dict = new ResourceDictionary();
            
            // 根据主题加载对应的资源字典
            string themeFile = theme switch
            {
                AppTheme.Light => "Themes/LightTheme.xaml",
                AppTheme.Dark => "Themes/DarkTheme.xaml",
                AppTheme.System => IsSystemDark() ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml",
                _ => "Themes/DarkTheme.xaml"
            };

            dict.Source = new Uri($"pack://application:,,,/{themeFile}", UriKind.Absolute);

            // 移除旧的主题字典，添加新的
            var resources = Application.Current.Resources;
            var oldDict = resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString?.Contains("Theme") == true);
            if (oldDict != null)
                resources.MergedDictionaries.Remove(oldDict);
            
            resources.MergedDictionaries.Add(dict);
        }

        private bool IsSystemDark()
        {
            // 通过注册表检测系统深色模式
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                return value is int i && i == 0;
            }
            catch
            {
                return false;
            }
        }

        private void SaveThemePreference(AppTheme theme)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "user_settings.json");
                JObject json;
                if (File.Exists(path))
                    json = JObject.Parse(File.ReadAllText(path));
                else
                    json = new JObject();
                
                json["Theme"] = theme.ToString();
                File.WriteAllText(path, json.ToString());
            }
            catch { }
        }

        private AppTheme LoadThemePreference()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "user_settings.json");
                if (File.Exists(path))
                {
                    var json = JObject.Parse(File.ReadAllText(path));
                    if (json["Theme"]?.ToString() is string themeStr && 
                        Enum.TryParse<AppTheme>(themeStr, out var theme))
                        return theme;
                }
            }
            catch { }
            return AppTheme.Dark; // 默认深色
        }
    }
}
