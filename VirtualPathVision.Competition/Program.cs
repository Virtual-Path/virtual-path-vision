using VirtualPathVision.Competition;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var options = CliOptions.Parse(args.Skip(1).ToArray());

try
{
    return command switch
    {
        "dataset" => DatasetCommands.Generate(options),
        "eval" => DatasetCommands.Evaluate(options),
        "agent" => AgentCommands.Run(options),
        "probe" => DatasetCommands.Probe(options),
        "aws" => AwsCommands.Run(options),
        "all" => RunAll(options),
        "help" or "--help" or "-h" => Help(),
        _ => Unknown(command)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"失败: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 3;
}

static int RunAll(CliOptions options)
{
    int rc = DatasetCommands.EnsureDataset(options);
    if (rc != 0) return rc;

    rc = DatasetCommands.Evaluate(options);
    if (rc != 0) return rc;

    rc = AgentCommands.Run(options);
    if (rc != 0) return rc;

    return AwsCommands.Run(options with { DryRun = true });
}

static int Help()
{
    Console.WriteLine("""
    VirtualPathVision 竞赛流水线

      dataset   生成可控缺陷数据集（PNG + labels.jsonl）
      eval      跑批量评测，输出 evaluation.json / evaluation.md / sweep.json
      agent     跑「感知 → 决策 → 动作」闭环，输出 agent-trace.jsonl 与证据图
      aws       上传证据到 S3、调用 Lambda 聚合、按需发 SNS；无凭据时加 --dry-run
      all       dataset（缺失时）+ eval + agent + aws --dry-run

    通用参数：
      --dataset <dir>       数据集目录（默认 competition/dataset）
      --out <dir>           产物目录（默认 artifacts）
      --count <n>           生成样本数（默认 800）
      --seed <n>            数据集种子（默认 20261026）
      --limit <n>           agent 模式最多处理多少样本
      --sensitivity <0..1>  eval 使用的灵敏度（默认 0.6）
      --dry-run             不访问 AWS，只打印计划
      --region <name>       AWS 区域（默认取 AWS_REGION，否则 us-east-1）
      --bucket <name>       S3 桶名（默认取 VPV_S3_BUCKET）
      --lambda <name>       Lambda 函数名（默认取 VPV_LAMBDA_FUNCTION）
      --topic <arn>         SNS 主题 ARN（默认取 VPV_SNS_TOPIC）

    示例：
      dotnet run --project VirtualPathVision.Competition -- all
      dotnet run --project VirtualPathVision.Competition -- eval --sensitivity 0.7
    """);
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"未知命令: {command}");
    return Help() == 0 ? 2 : 2;
}
