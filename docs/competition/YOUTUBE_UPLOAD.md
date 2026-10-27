# YouTube upload package — VirtualPath Vision demo

## File

- Video: `C:\Users\Reality\Videos\VirtualPathVision-Demo-2026.mp4` (local, not committed)
- Duration: **1:26**
- Format: 1920×1080, 30 fps, H.264, AAC mono
- Thumbnail: `docs/competition/youtube-thumbnail.jpg` (1280×720)
- Transcript: `docs/competition/VIDEO_TRANSCRIPT_EN.md`

**Status: uploaded and submitted on Devpost on 2026-10-27.**

## Upload settings

- **Visibility:** Unlisted
- **Title:** `VirtualPath Vision — Agentic Surface Inspection | OpenCV AI Competition 2026`
- **Video language:** English
- **Audience:** Not made for kids
- **Thumbnail:** upload `docs/competition/youtube-thumbnail.jpg`
- **Category:** Science & Technology (or Education)
- **Tags:** `OpenCV, OpenCvSharp, computer vision, machine vision, industrial inspection, surface defect, agentic AI, C#, .NET 8, WPF, AWS, 3D rendering, VirtualPath-Core, OpenCV AI Competition 2026, MJPEG, JSONL`

## Description (copy-paste)

```text
VirtualPath Vision — Agentic Surface Inspection
OpenCV AI Competition 2026

A scratch thinner than a hair can fail an entire production batch. VirtualPath Vision is an agentic inspection system for surface defects: OpenCV 5 finds scratches, spots and stains; an agent reinspects ambiguous parts; and every step is written to a replayable JSONL trace.

The camera feed in this demo is rendered by VirtualPath-Core, our own 3D engine, and served as a headless MJPEG stream on /cam1. The desktop app consumes it through a network stream, so the same client works with the virtual camera or an industrial camera.

What the demo shows
0:00 Intro
0:08 Virtual camera: VirtualPath-Core → MJPEG /cam1
0:22 Surface detection: NG Scratch score 0.56, 7.7 ms
0:35 Agent re-inspection: P0401 reinspected, trace recorded
0:50 Benchmark: 800 synthetic parts, precision 100%, recall 82.5%, F1 90.41, p95 8.79 ms
1:09 Code, evidence and AWS path

Results (800 synthetic parts)
- Precision 100.00%, Recall 82.50%, F1 90.41%, image-level accuracy 91.25%
- Defect-class accuracy 86.67%, mean IoU 0.613
- p50 7.88 ms / p95 8.79 ms, 128.2 FPS
- 211/800 parts vision-triggered re-inspection; 85.5% agreement with ground truth
- Known limits: 17 px edge blind spot; synthetic benchmark; AWS dry-run receipt only; MES/PLC not commissioned.

Code and evidence
- Vision: https://github.com/Virtual-Path/virtual-path-vision
- Engine: https://github.com/Virtual-Path/virtual-path-core
- Technical report: https://github.com/Virtual-Path/virtual-path-vision/blob/master/docs/competition/TECHNICAL_REPORT.md
- Artifacts: https://github.com/Virtual-Path/virtual-path-vision/tree/master/artifacts

Built with: OpenCV 5, OpenCvSharp, C#, .NET 8, WPF, AWS (S3, Lambda, CloudWatch, SNS), Modbus TCP, OPC UA, SQLite, Avalonia, OpenGL ES, Silk.NET, MJPEG, JSONL, Windows.

AWS note: the repository includes a CloudFormation template and a dry-run receipt. No live AWS deployment is claimed in this video.
```

## Upload checklist

1. Open YouTube Studio → **Create** → **Upload videos**.
2. Select `C:\Users\Reality\Videos\VirtualPathVision-Demo-2026.mp4`.
3. Paste the title and description above; add the tags.
4. Set visibility to **Unlisted**; select **Not made for kids**.
5. Upload `docs/competition/youtube-thumbnail.jpg` as the thumbnail.
6. Wait until the HD (1080p) version is processed.
7. Copy the full watch URL (`https://www.youtube.com/watch?v=...`).
8. Paste that URL into the Devpost **Video demo** field. A GitHub link or local file will not work there.
9. Check the embedded player on Devpost before submitting.

## Claims to keep

- The 800-part benchmark is synthetic.
- The 17 px edge blind spot is a known limitation.
- AWS is a dry-run receipt only; no live deployment is claimed.
- The USB/MSMF camera path and MES/PLC integration are not commissioned in this demo.
- VirtualPath-Core is the project's own 3D engine, not an external dependency.
