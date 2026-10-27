# Devpost fields — copy-paste sheet

Use **`DEVPOST_PROJECT_STORY.md`** for the story. Do not use the older `DEVPOST_STORY.md`.

## Basics

- **Project name:** `VirtualPath Vision — Agentic Surface Inspection`
- **Short name / title:** `VirtualPath Vision`
- **Elevator pitch:**

```text
Agentic industrial vision: OpenCV 5 finds scratches, spots and stains, an agent re-inspects uncertain parts, and every decision is traced—with a 3D engine serving as the virtual camera.
```

- **Video demo:** paste the full YouTube watch URL after upload (Unlisted). Do not paste a GitHub link or a local file path.
- **Upload a File:** `C:\Users\Reality\Videos\VirtualPathVision_Competition_Evidence.zip` (6.4 MB, under the 35 MB limit). The same zip is at `artifacts/demo/VirtualPathVision_Competition_Evidence.zip` (Git-ignored).
- **Repository URL:** `https://github.com/Virtual-Path/virtual-path-vision`

## Sponsor / Special Prizes

- **Best Use of COOL Award:** leave unchecked. The project does not add or claim a COOL-specific integration; do not invent one.
- **Agentic Vision Award:** check it. The project has a vision-driven re-inspection loop, a JSONL audit trail, a human-in-the-loop quarantine path, and guardrails.
- **Special Award Consideration:** if a second checkbox group appears, select **Agentic Vision Award** only. Leave **Best Use of COOL** unchecked unless you have actually integrated COOL.

## Testing instructions (paste-ready)

```text
No hosted public web endpoint. This is a Windows 10/11 x64 desktop + CLI project; reproduce locally.

Prerequisites: Windows 10/11 x64, .NET 8 SDK, Git.

1) Clone and build the vision app:
git clone https://github.com/Virtual-Path/virtual-path-vision.git
cd virtual-path-vision
dotnet build VirtualPathVision.sln
dotnet run --project VirtualPathVision.Tests

2) Reproduce the 800-part benchmark, agent trace and dry-run AWS receipt:
dotnet run --project VirtualPathVision.Competition -- all

Expected outputs under artifacts/: evaluation.json, agent-summary.json, agent-trace.jsonl, aws-receipt.json (mode "dry-run"). No AWS account is required for this step.

3) Optional desktop UI with the virtual camera:
git clone https://github.com/Virtual-Path/virtual-path-core.git
cd virtual-path-core/VirtualPath-EngineClient
dotnet run --project VirtualPathCore.CameraBridge -- --serve --port 8080

In a second terminal, from virtual-path-vision:
dotnet run --project VirtualPathVision/VirtualPathVision.csproj

In the app: choose Network Stream, host 127.0.0.1, port 8080, path /cam1, click Connect, select Surface Defect, then start the camera. Expected: P0400 -> NG Scratch score≈0.56. P0401 is a documented class-confusion failure (NG Spot).

AWS note: the CloudFormation template is included, but this submission uses a dry-run receipt only. No live AWS deployment is claimed. The physical USB/MSMF path and MES/PLC integration are not commissioned.
```

## Built with — exactly 25 tags

```text
OpenCV
OpenCvSharp
C#
.NET 8
WPF
AWS
Amazon S3
AWS Lambda
Amazon CloudWatch
Amazon SNS
AWS CloudFormation
Modbus TCP
OPC UA
SQLite
Avalonia
OpenGL ES
Silk.NET
3D Rendering
MJPEG
Computer Vision
Machine Vision
Industrial Automation
Agentic AI
JSONL
Windows
```

## Try it out

```text
https://github.com/Virtual-Path/virtual-path-vision
https://github.com/Virtual-Path/virtual-path-core
https://github.com/Virtual-Path/virtual-path-vision/blob/master/docs/competition/TECHNICAL_REPORT.md
https://github.com/Virtual-Path/virtual-path-vision/tree/master/artifacts
```

## Media order

Upload in this order; set the first as the cover:

1. `docs/competition/devpost-media/01_surface_camera_3x2.png` — **cover**
2. `docs/competition/devpost-media/02_engine_virtual_camera_3x2.png`
3. `docs/competition/devpost-media/04_surface_evidence_3x2.png`
4. `docs/competition/devpost-media/05_evidence_grid_3x2.png`

Optional fifth image: `03_surface_inspection_3x2.png`. Its lower half is mostly empty; do not use it as the cover.

Captions: `docs/competition/devpost-media/CAPTIONS_EN.md`.

## Story

`docs/competition/DEVPOST_PROJECT_STORY.md` — seven sections: Inspiration, What it does, How we built it, Challenges, Accomplishments, What we learned, What's next.

## Claims to keep

- 800-part benchmark is **synthetic**.
- **17 px** edge blind spot.
- AWS is a **dry-run receipt** only; no live deployment claim.
- MES/PLC and physical USB/MSMF paths are **not commissioned**.
- VirtualPath-Core is the project's **own 3D engine**, not an external dependency.
- The demo video uses a clean earlier Game Bar recording plus verified screenshots/trace evidence; it is not a new live production-line recording.

## Final checks

- [ ] YouTube upload is Unlisted and the HD version is processed.
- [ ] Video URL embeds on Devpost.
- [ ] Cover is `01_surface_camera_3x2.png`.
- [ ] All seven story sections are present.
- [ ] 25 built-with tags are selected.
- [ ] Four try-it-out links are present.
- [ ] Screenshot the submission confirmation.
