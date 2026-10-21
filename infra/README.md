# AWS 侧部署与验证

这条链路只做三件事，避免把竞赛变成"堆云服务"：

1. **S3 证据湖**：每次运行把标注后的证据图、`evaluation.json`、`agent-summary.json` 上传到版本化、服务端加密的桶；
2. **Lambda 聚合**：`vpv-defect-aggregator` 汇总本次运行的放行/剔除/隔离/升级数量，写入 CloudWatch 指标；
3. **SNS 复核通知**：出现隔离或升级样本时通知质量负责人。

边缘端（本仓库的 WPF 程序与竞赛流水线）负责 OpenCV 5 检测与动作决策，AWS 只承担留存、聚合与通知。

## 1. 前置条件

- AWS CLI v2，且已完成 `aws configure` / SSO 登录（`aws sts get-caller-identity` 能返回账号）。
- 权限：创建 S3、SNS、Lambda、IAM 角色、CloudWatch 指标。
- 区域建议与产线就近，例如 `ap-northeast-1`。

## 2. 一键部署

```powershell
aws cloudformation deploy `
  --template-file infra/template.yaml `
  --stack-name vpv-inspection `
  --capabilities CAPABILITY_NAMED_IAM `
  --parameter-overrides AlertEmail=quality@example.com
```

`AlertEmail` 可省略；省略时不会创建邮件订阅。

查看输出：

```powershell
aws cloudformation describe-stacks `
  --stack-name vpv-inspection `
  --query "Stacks[0].Outputs" --output table
```

会得到 `BucketName`、`TopicArn`、`FunctionName`。

> 订阅是双重确认（double opt-in）：第一次部署后，收件人会收到 "AWS Notification - Subscription Confirmation" 邮件，必须点确认，SNS 才会真正投递。

## 3. 配置边缘端

```powershell
$env:AWS_REGION            = "ap-northeast-1"
$env:VPV_S3_BUCKET         = "<BucketName 输出>"
$env:VPV_LAMBDA_FUNCTION   = "<FunctionName 输出>"
$env:VPV_SNS_TOPIC         = "<TopicArn 输出>"
```

凭据使用 AWS SDK 默认链（环境变量、`~/.aws`、SSO、实例角色均可），程序里不落任何密钥。

## 4. 跑通并验证

```powershell
# 1) 本地全流程（数据集 → 评测 → 代理闭环 → dry-run）
dotnet run --project VirtualPathVision.Competition -- all

# 2) 真实上传 + 调用 Lambda + 按需发 SNS
dotnet run --project VirtualPathVision.Competition -- aws
```

验收点（缺一不可）：

```powershell
# 桶里能看到本次 runId 前缀下的对象
aws s3 ls "s3://$env:VPV_S3_BUCKET/detections/" --recursive | Select-Object -Last 10

# Lambda 返回聚合结果（Competition 的控制台也会打印）
aws lambda invoke `
  --function-name $env:VPV_LAMBDA_FUNCTION `
  --payload '{"runId":"smoke","agent":{"byAction":{"Pass":10,"Reject":2}}}' `
  out.json ; Get-Content out.json

# CloudWatch 指标已产生
aws cloudwatch list-metrics --namespace "VPV/Inspection"

# 如需手工发一条通知
aws sns publish --topic-arn $env:VPV_SNS_TOPIC `
  --subject "VPV smoke test" --message "pipeline ok"
```

`artifacts/aws-receipt.json` 会记录 `mode`、上传对象清单、Lambda 返回值和 `snsMessageId`，这是提交材料里的云端证据。

## 5. 没有凭据时

`aws` 命令在未配置 `VPV_S3_BUCKET` 或显式加 `--dry-run` 时不会发起任何网络请求，只输出计划和 `mode: dry-run` 的回执。这样可以先在本机把整条流水线跑通，再补云端证据。

## 6. 清理

```powershell
# 桶是版本化的，先清空所有版本
aws s3 rm "s3://$env:VPV_S3_BUCKET" --recursive
aws cloudformation delete-stack --stack-name vpv-inspection
```

生命周期规则会在 90 天后自动过期证据对象，非当前版本保留 30 天。
