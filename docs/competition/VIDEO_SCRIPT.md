# Video script (≤ 5:00)

Target length **4:45**. English narration, English UI, 1920×1080, 30 fps. Record with OBS; use a clean desktop (no unrelated windows, no personal notifications). Mouse out of the frame before each capture.

The goal is not to show every feature. It is to make three things undeniable:

1. the vision runs on OpenCV 5 frames (virtual camera included),
2. the agent's next action changes because of what it saw,
3. there is a measured benchmark with honest failure cases.

---

## 0:00–0:25 · Problem

**Screen:** the WPF console running with the VirtualPath-Core camera, production line card visible.

**Narration:**
> "A production line does not need another detector demo. It needs a system that can say how good it is, what to do when the image is bad, and why a part was rejected. VirtualPath Vision is an industrial vision console built on OpenCV 5. This submission closes that loop."

## 0:25–1:10 · Acquisition, including the virtual camera

**Screen:** camera panel; then the engine console window with the MJPEG server log; then the vision app connected to `http://127.0.0.1:8081/cam1`.

**Narration:**
> "Frames come from a USB camera, or from a virtual camera: the VirtualPath-Core engine renders a 3D production line headless and serves it as MJPEG. The vision app consumes it with the same NetworkStream path as a real camera — zero changes to the application. This is what makes the demo reproducible on any machine."

**Shot tip:** show the engine window title and the `/cam1` request in the log for two seconds.

## 1:10–2:05 · Detection and evidence

**Screen:** run `dotnet run --project VirtualPathVision.Competition -- probe --index 401`; show the printed finding and open `artifacts/probe-0401.jpg`.

**Narration:**
> "The detector finds the part with an Otsu mask, excludes seventeen pixels of border — the border gradient is as strong as a scratch — and runs two Gaussian scales. The fine scale keeps a two-pixel scratch's shape; the coarse scale sees low-contrast stains that the fine scale would reduce to a ring. Each finding gets a score, a class and a bounding box. This image is the evidence packet: verdict, score, focus, exposure and latency, rendered into the frame."

**Shot tip:** zoom into the header band (verdict + score) and the bounding box for three seconds.

## 2:05–3:00 · The agentic step

**Screen:** open `artifacts/agent-trace.jsonl`, filter to `P0401`, show the two `inspect` calls side by side.

**Narration:**
> "This is the part that matters. The first inspection was ambiguous: spot, score 0.41, below the direct-reject line. The agent did not guess. It re-inspected with sensitivity 0.85 and contrast enhancement. The second observation changed the class to stain — still above threshold — so the part was rejected with the reason 'confirmed by re-inspection'. Two hundred eleven of eight hundred parts went through a vision-driven re-inspection. Every call, its arguments and its observation are in the trace."

**Shot tip:** highlight `sensitivity: 0.6 → 0.85` and `class: Spot → Stain`.

## 3:00–3:50 · Benchmark and failure cases

**Screen:** `artifacts/evaluation.md`, then `docs/competition/evidence-grid.png`, then `artifacts/sweep.md`.

**Narration:**
> "Eight hundred synthetic parts with exact labels: 400 clean, 152 scratches, 152 spots, 96 stains. At the default operating point: one hundred percent precision, zero false alarms, 82.5 percent recall, F1 90.4, p95 under nine milliseconds at 128 frames per second. The sweep shows the trade-off instead of hiding it: at sensitivity 0.75 recall rises to 85.8 percent for 1.4 points of precision. And these are the failures — a low-contrast stain missed entirely, and a faint scratch cleared by re-inspection. They are in the report, not edited out."

## 3:50–4:30 · AWS evidence

**Screen:** AWS console S3 bucket with the run prefix; Lambda test returning the aggregate; CloudWatch metric `ItemsRequiringReview`; `artifacts/aws-receipt.json`.

**Narration:**
> "The edge keeps the ten-millisecond loop. AWS keeps the audit trail: versioned S3 for evidence, a Lambda that aggregates pass, reject and quarantine counts into CloudWatch, and SNS for the parts that need a human. The receipt records every uploaded object, the Lambda response and the notification id."

> If AWS is not configured on the recording machine, say so and show the dry-run receipt: *"This is the dry-run receipt; the live path is one command away and documented in infra/README."*

## 4:30–4:45 · Close

**Screen:** the console with the production line running; then the repo README.

**Narration:**
> "OpenCV 5 for the eyes, an agent for the judgment, AWS for the evidence. The code, the benchmark and the failure analysis are in the repository. Thank you."

---

## Recording checklist

- [ ] English UI (`user_settings.json` language = en) and English console output.
- [ ] Terminal font ≥ 16 pt; no PowerShell errors in frame.
- [ ] Close Outlook/Slack/Steam; hide the taskbar clock if it shows personal info.
- [ ] Record at 1080p30, CBR 8 Mbps; verify the final file is **under 5:00**.
- [ ] Upload to YouTube (unlisted is fine) or Devpost's video host; check the link in an incognito window.
- [ ] Watch the recording once end to end: no dead air, no cut-off text, numbers match the report.

## Commands used on camera

```powershell
# console app
dotnet run --project VirtualPathVision

# engine virtual camera (separate repository)
dotnet run --project VirtualPath-EngineClient\VirtualPathCore.CameraBridge -- --serve --port 8081

# competition pipeline
dotnet run --project VirtualPathVision.Competition -- all
dotnet run --project VirtualPathVision.Competition -- probe --index 401
dotnet run --project VirtualPathVision.Competition -- agent
dotnet run --project VirtualPathVision.Competition -- aws
```
