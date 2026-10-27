# VirtualPath Vision — Competition Evidence Package

OpenCV AI Competition 2026  
Project: **VirtualPath Vision — Agentic Surface Inspection**

This archive is the judge-facing evidence bundle for the Devpost submission. The full source code is in the public repositories below; the video is linked from the Devpost video field.

- Vision repository: https://github.com/Virtual-Path/virtual-path-vision
- 3D engine: https://github.com/Virtual-Path/virtual-path-core
- Technical report: https://github.com/Virtual-Path/virtual-path-vision/blob/master/docs/competition/TECHNICAL_REPORT.md
- Artifacts: https://github.com/Virtual-Path/virtual-path-vision/tree/master/artifacts

## What is inside

| Path | Content |
|---|---|
| `DEVPOST_PROJECT_STORY.md` | Seven-section project story (Inspiration → What's next) |
| `TECHNICAL_REPORT.md` | Authoritative design, metrics, failure analysis and limitations |
| `ARCHITECTURE.md` / `architecture.svg` | System architecture and data flow |
| `devpost-media/` | Five 3:2 presentation images used in the submission |
| `images/` | Full-size desktop and evidence screenshots |
| `evaluation.json` | 800-part benchmark metrics and per-class results |
| `sweep.json` | Threshold sensitivity sweep |
| `agent-summary.json` | Agent action counts and ground-truth agreement |
| `agent-trace.jsonl` | One JSONL record per part; every tool call with arguments and observation |
| `evidence/` | Annotated evidence images for pass, reject, quarantine and escalate cases |
| `aws-receipt.en.json` | English translation of the dry-run AWS receipt (same fields) |
| `template.yaml` | AWS CloudFormation stack: S3, Lambda, CloudWatch, SNS |
| `VIDEO_TRANSCRIPT_EN.md` | English narration transcript and timestamps |

## Reproduce

Prerequisites: Windows 10/11 x64, .NET 8 SDK, Git.

```powershell
git clone https://github.com/Virtual-Path/virtual-path-vision.git
cd virtual-path-vision
dotnet build VirtualPathVision.sln
dotnet run --project VirtualPathVision.Tests
dotnet run --project VirtualPathVision.Competition -- all
```

The last command regenerates the synthetic 800-part dataset, runs the detector and the agent loop, and writes the artifacts plus a dry-run AWS receipt. It does not require an AWS account.

Optional desktop UI with the virtual camera:

```powershell
git clone https://github.com/Virtual-Path/virtual-path-core.git
cd virtual-path-core/VirtualPath-EngineClient
dotnet run --project VirtualPathCore.CameraBridge -- --serve --port 8080
```

In a second terminal, from `virtual-path-vision`:

```powershell
dotnet run --project VirtualPathVision/VirtualPathVision.csproj
```

Choose **Network Stream**, host `127.0.0.1`, port `8080`, path `/cam1`, then **Connect**, select **Surface Defect**, and start the camera. P0400 reports `NG Scratch score=0.56`; P0401 is a documented class-confusion failure (`NG Spot`).

## Scope and honesty notes

- The 800-part benchmark is **synthetic**, generated from a fixed seed.
- Defects within **17 px** of the part boundary are not inspected; this is a known limitation.
- AWS is a **dry-run receipt only**. The CloudFormation template is included, but no live deployment is claimed.
- The physical USB/MSMF camera path and MES/PLC integration are **not commissioned** in this submission.
- `VirtualPath-Core` is the project's **own 3D engine**, not an external dependency.
- The desktop screenshots show an MJPEG run; scores can differ slightly from the PNG benchmark because of JPEG re-encoding (P0401: 0.38 in the desktop run, 0.41 in the PNG trace).

## Key metrics

- Precision **100.00%**, Recall **82.50%**, F1 **90.41%**, image accuracy **91.25%**
- Defect-class accuracy **86.67%**, mean IoU **0.613**
- p50 **7.88 ms**, p95 **8.79 ms**, **128.2 FPS**
- **211/800** parts triggered a vision-driven re-inspection; **85.5%** agreement with ground truth
- Final actions: 514 pass, 240 reject, 45 quarantine, 1 escalate
