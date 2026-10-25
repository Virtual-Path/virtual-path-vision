# Short demo video script (≈ 1:20)

Use this if there is no time for the full 4:55 script. Record at 1080p30, English UI, one clean take. Upload to YouTube as **Unlisted** and paste the watch URL into Devpost.

## 0:00–0:10 · What it is

**Screen:** the WPF console connected to the VirtualPath-Core MJPEG camera.

**Narration:**
> "VirtualPath Vision is an industrial surface-inspection console built on OpenCV 5. Frames come from a real camera, or from a 3D engine acting as a virtual camera."

## 0:10–0:30 · Detection and evidence

**Screen:** Surface Defect mode; show the live result `NG Scratch score=0.56`, then open the saved evidence PNG.

**Narration:**
> "The detector uses an Otsu part mask, a seventeen-pixel border exclusion, and two Gaussian scales. Here it finds a scratch at 0.56 and writes an evidence image with the verdict, score, focus, exposure, and latency."

## 0:30–0:50 · The agentic step

**Screen:** `artifacts/agent-trace.jsonl`, filtered to `P0401`; highlight `sensitivity 0.6 → 0.85`, `Spot → Stain`.

**Narration:**
> "When the evidence is ambiguous, the agent changes the next inspection instead of guessing. For P0401 it re-inspected at sensitivity 0.85 with contrast enhancement. The class changed from spot to stain, and the part was rejected as confirmed by re-inspection. Two hundred eleven of eight hundred parts triggered a vision-driven re-inspection."

## 0:50–1:05 · Benchmark

**Screen:** `artifacts/evaluation.md` and `docs/competition/evidence-grid.png`.

**Narration:**
> "On eight hundred synthetic parts: one hundred percent precision, 82.5 percent recall, F1 90.4, and p95 under nine milliseconds. The failure cases stay in the report."

## 1:05–1:20 · AWS and close

**Screen:** `artifacts/aws-receipt.json`, then the repository README.

**Narration:**
> "AWS stores evidence in S3, aggregates with Lambda, and alerts through SNS. This is the dry-run receipt; the live deployment is documented and one command away. The code, benchmark, and failure analysis are public. Thank you."

## Upload checklist

- [ ] Final file is MP4, 1080p30, and under 5 minutes.
- [ ] No notifications, personal files, or unrelated windows in frame.
- [ ] YouTube visibility is **Unlisted**, not Private.
- [ ] Play the link in an incognito window before pasting it into Devpost.
- [ ] Paste the full `https://www.youtube.com/watch?v=...` URL, not a local path or GitHub link.
