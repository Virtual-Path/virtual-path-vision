# Submission checklist

Deadline: **2026-10-27 14:45 GMT+8** (= 2026-10-26 23:59 PT). Judging 10-27 → 11-09; winners announced 11-10 on OpenCV Live.

## Hard requirements

| # | Requirement | Status | Evidence / action |
|---|---|---|---|
| 1 | Uses OpenCV 5 for substantive image analysis | **Done** | `OpenCvSharp5 5.0.0.20260905`; `DefectDetector` (Otsu, scale space, morphology, contours); 800-part benchmark |
| 2 | A meaningful component runs on AWS | **Code + template done, live run BLOCKED** | `infra/template.yaml` (S3 + Lambda + CloudWatch + SNS), `VirtualPathVision.Competition aws`; needs AWS credentials to produce a live receipt |
| 3 | Technical report | **Done** | `docs/competition/TECHNICAL_REPORT.md` |
| 4 | Repository accessible to judges | **Done** | GitHub repo is public (`gh repo view`: `visibility: PUBLIC`); judges can clone it |
| 5 | Dependencies pinned | **Done** | exact versions in the `.csproj` files; dataset seed fixed |
| 6 | Architecture diagram | **Done** | `docs/competition/architecture.svg`, Mermaid in `ARCHITECTURE.md` |
| 7 | Runnable endpoint or live demo | **Done (app); video recorded, YouTube upload pending** | engine MJPEG endpoint `/cam1` + desktop Surface Defect mode verified over MJPEG; evidence PNG written; demo MP4 at `artifacts/demo/VirtualPathVision-Demo-2026.mp4` (Git-ignored) |
| 8 | Video ≤ 5 minutes | **Recorded (1:26); upload pending** | `artifacts/demo/VirtualPathVision-Demo-2026.mp4`; package in `YOUTUBE_UPLOAD.md`; transcript in `VIDEO_TRANSCRIPT_EN.md` |
| 9 | Evaluation evidence incl. failure cases | **Done** | §7–§8 of the report, `artifacts/evaluation.json`, `docs/competition/evidence-grid.png` |
| 10 | Devpost submission | **Pending** | needs the user's Devpost account |

## Bonus track: Agentic Vision ($1,000)

| Criterion | Evidence |
|---|---|
| Visual output changes the next action | `InspectionAgent` re-inspects with different parameters; 211/800 parts (26.4 %) had a vision-driven re-plan |
| Audit trail | `artifacts/agent-trace.jsonl`, every tool call with args + observation + reason code |
| Human in the loop | stains ≥ 0.50 are quarantined (45 parts), not auto-scrapped; SNS review alert |
| Guardrails | unusable image → escalate, never pass; unclear score → re-inspect, never guess |

## Before submitting

- [ ] **AWS live run.** Configure credentials, deploy `infra/template.yaml`, run `-- aws`, keep `artifacts/aws-receipt.json` with `"mode": "aws"`, plus the S3 object list, Lambda response and SNS message id. Paste the S3/Lambda section into the Devpost "How we built it" or the report appendix.
- [x] **Desktop integration verified.** Surface Defect mode ran against a live MJPEG stream: `NG Scratch score=0.56`, annotated evidence written; screenshot `docs/images/14_surface_camera.png`. Known failure `0401` (truth scratch → Spot 0.38) kept in §8.6.
- [x] **Record the video.** 1:26, 1080p30, English narration and on-screen captions; uses the clean Game Bar live clip plus verified screenshots and trace evidence. File is Git-ignored.
- [ ] **Upload the video to YouTube as Unlisted** and paste the watch URL into Devpost. Follow `YOUTUBE_UPLOAD.md`; wait for HD processing before checking the embed.
- [x] **Repo visibility checked** (`gh repo view --json visibility` → `PUBLIC`).
- [ ] **Fresh-clone test** on another machine or a clean folder:
      `dotnet build VirtualPathVision.sln` → `dotnet run --project VirtualPathVision.Tests` → `dotnet run --project VirtualPathVision.Competition -- all`.
- [ ] **Re-run the benchmark** after any code change and update the numbers in the report (they are measured, not aspirational).
- [ ] **Devpost fields**: title, tagline, "what it does", "how we built it", "challenges", "accomplishments", "what we learned", "what's next", built-with tags (OpenCV, C#, .NET, AWS Lambda, Amazon S3, WPF), repo URL, video URL.
- [ ] **Screenshot** of the Devpost submission confirmation.

## Honest gaps (do not hide these)

1. **AWS live path** is implemented and dry-run verified, but not yet exercised against a real account.
2. **Synthetic benchmark**, not a real production line. The report states this explicitly and explains why.
3. **Edge exclusion** means defects within 17 px of the part boundary are not inspected; the dataset keeps defects inside that margin, so the recall number does not cover it.
4. **Physical USB camera path** is not re-verified in this round; the virtual camera (VirtualPath-Core MJPEG) is the demonstrated acquisition path.

## What is already proven locally

- `dotnet build VirtualPathVision.sln`: 0 warnings, 0 errors.
- `VirtualPathVision.Tests`: 117 assertions (87 production-line + 30 inspection), 0 failures.
- 800-part evaluation: precision 100.00 %, recall 82.50 %, F1 90.41 %, p95 8.79 ms.
- Agent loop: 514 pass / 240 reject / 45 quarantine / 1 escalate; 211 vision-driven re-inspections.
- Desktop Surface Defect mode, live MJPEG: 0400 (scratch) → `NG Scratch 0.56` + evidence PNG; 0401 (scratch) → `NG Spot 0.38` (MJPEG re-encode; `0.41` in the PNG trace; documented class confusion).
- Deterministic artifacts in `artifacts/` (evaluation, sweep, trace, summary, actuator log, 12 evidence images, AWS dry-run receipt).
