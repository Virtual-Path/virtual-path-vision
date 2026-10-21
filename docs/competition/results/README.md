# Recorded run — 2026-10-21

This directory is a snapshot of one full pipeline run so judges can inspect the evidence without
building the project. It is **generated output**, not hand-written.

| File | Producer |
|---|---|
| `evaluation.json` / `.md` | `VirtualPathVision.Competition eval` |
| `sweep.json` / `.md` | `VirtualPathVision.Competition eval` (sensitivity sweep) |
| `agent-summary.json` | `VirtualPathVision.Competition agent` |
| `agent-trace.jsonl` | one line per part, every tool call with arguments and observation |
| `actuator.log` | commands emitted by the simulated line actuator |
| `aws-receipt.json` | `VirtualPathVision.Competition aws` — **dry-run** on this machine |

Environment: Windows, .NET 8, `OpenCvSharp5 5.0.0.20260905`, dataset seed `20261026`, 800 samples.
Latency figures are machine-dependent; precision/recall/class accuracy are deterministic for this
dataset and detector version.

The AWS receipt is `"mode": "dry-run"` because no AWS credentials were available during the build.
With credentials, the same command writes a receipt with the uploaded object list, the Lambda
response and the SNS message id. See `infra/README.md` for the exact verification steps.

Reproduce:

```powershell
dotnet run --project VirtualPathVision.Competition -- all
```
