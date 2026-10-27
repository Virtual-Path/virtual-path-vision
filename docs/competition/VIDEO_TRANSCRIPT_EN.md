# Demo video transcript — VirtualPath Vision

Video: submitted Devpost demo (YouTube, 2026-10-27)  
Duration: 1:26  
Narration: English (Microsoft Zira, Windows SAPI)

## 0:00 — Intro / virtual camera

A scratch thinner than a hair can fail an entire production batch. VirtualPath Vision is an agentic inspection system built for that moment.

## 0:08 — VirtualPath-Core / MJPEG

The camera feed is real. VirtualPath-Core, our own 3D engine, renders the line headless and serves it as an M-J-PEG stream on slash cam one. The desktop app consumes it like any industrial camera.

## 0:22 — Surface detection

Inside the app, a two-scale OpenCV five detector searches for scratches, spots, and stains. Here it reports an N-G scratch, score zero point five six, and saves the frame as evidence.

## 0:36 — Agent re-inspection

When a part is ambiguous, the agent does not guess. It reinspects. Part zero four zero one is first read as a spot, then re-examined at higher sensitivity, corrected to a stain, and recorded in a JSON-L trace.

## 0:51 — Benchmark

On eight hundred synthetic parts: precision one hundred percent, recall eighty two point five percent, and F one is ninety point four one. P ninety five latency is eight point seven nine milliseconds. Two hundred eleven parts were re-inspected, and eighty five point five percent matched ground truth.

## 1:10 — Code, evidence and AWS

Code, benchmark, evidence, and failure analysis are public. The AWS evidence path ships as S3, Lambda, CloudWatch, and SNS, with a dry-run receipt. VirtualPath Vision turns every inspection into a traceable decision.

## On-screen technical notes

- P0400 result: `NG Scratch score=0.56`, `1 finding`, `7.7 ms`.
- P0401 trace: first `inspect(sensitivity=0.60)` → `NG Spot 0.41` (2 findings); then `reinspect(sensitivity=0.85, contrast on)` → `NG Stain 0.29` (1 finding); action `Reject`, reason `confirmed_by_reinspection`.
- Benchmark: 800 synthetic parts; Precision 100.00%, Recall 82.50%, F1 90.41%, image accuracy 91.25%, class accuracy 86.67%, mean IoU 0.613; p50 7.88 ms, p95 8.79 ms, 128.2 FPS.
- Agent: 211/800 vision-triggered re-inspections; 85.5% agreement with ground truth.
- Limits: 17 px edge blind spot; AWS dry-run only; MES/PLC not commissioned.
