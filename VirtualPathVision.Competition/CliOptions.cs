namespace VirtualPathVision.Competition;

/// <summary>
/// 极简命令行参数解析：只支持 <c>--key value</c> 与 <c>--flag</c>。
/// 竞赛流水线要保持零依赖，所以没有引入 System.CommandLine。
/// </summary>
public sealed record CliOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>不访问 AWS，只打印计划。</summary>
    public bool DryRun { get; init; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;

            string key = token[2..];
            string? value = null;

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            options._values[key] = value;
        }

        return options with { DryRun = options.Get("dry-run") != null };
    }

    /// <summary>读取原始值；不存在返回 null（flag 形式返回空串）。</summary>
    public string? Get(string name)
        => _values.TryGetValue(name, out var value) ? value ?? "" : null;

    public string GetString(string name, string fallback)
        => Get(name) is { Length: > 0 } value ? value : fallback;

    public int GetInt(string name, int fallback)
        => int.TryParse(Get(name), out int value) ? value : fallback;

    public double GetDouble(string name, double fallback)
        => double.TryParse(Get(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : fallback;

    /// <summary>解析为绝对路径，相对路径按当前工作目录展开。</summary>
    public string GetPath(string name, string fallback)
        => Path.GetFullPath(GetString(name, fallback));

    public string GetEnv(string name, string envName, string fallback)
        => Get(name) is { Length: > 0 } value
            ? value
            : Environment.GetEnvironmentVariable(envName) is { Length: > 0 } env
                ? env
                : fallback;
}

/// <summary>流水线各目录的默认位置（相对仓库根目录）。</summary>
public static class Paths
{
    public static string Dataset(CliOptions options)
        => options.GetPath("dataset", Path.Combine("competition", "dataset"));

    public static string Artifacts(CliOptions options)
        => options.GetPath("out", "artifacts");
}
