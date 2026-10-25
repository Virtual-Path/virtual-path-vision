## Inspiration

A surface defect is a small visual event with a large operational consequence. A scratch, a spot, or a faint stain can stop a line, trigger a re-check, or ship a bad part. The hard part is not drawing a box around the defect. The hard part is deciding what to do when the evidence is ambiguous.

Most demos stop at a bounding box. A real line needs a next action and a reason. A score of 0.41 on a possible scratch does not tell an operator whether to reject the part, re-photograph it, or call a quality engineer. We wanted a vision system where the image changes the next action, and where every one of those actions is traceable.

We already had a WPF machine-vision console that could stream frames and run classic OpenCV tools. It could not answer three questions a production engineer actually asks:

1. How good is the inspection?
2. What happens when the image is bad?
3. Can we prove why a part was rejected?

The OpenCV AI Competition was a forcing function to close that loop. We also wanted the demo to be reproducible without a factory, so we built a companion 3D engine, **VirtualPath-Core**, that renders a production-line scene headless and serves it as MJPEG. The vision app consumes it like any other camera. The whole pipeline runs on a laptop.

## What it does

**VirtualPath Vision** is a Windows industrial vision console built with .NET 8, WPF, and OpenCV 5. The competition submission adds a complete surface-inspection path:

- **Surface Defect mode** — the 12th processing mode in the operator console. It reuses the production-line ROI, runs the same detector as the benchmark, maps finding rectangles back to the full frame, and writes an annotated evidence image when a part transitions to NG.
- **A two-scale OpenCV 5 detector** for scratches, spots, and low-contrast stains. Otsu thresholding isolates the part, a Gaussian scale space removes the brushed-metal background, and two scales cover both thin scratches and large faint stains.
- **An agentic re-inspection loop** — if the part is missing, out of focus, or the score is ambiguous, the agent changes the next tool call: re-acquire, re-inspect with CLAHE and a different sensitivity, quarantine for human review, reject, or pass. Every step is written to JSONL with arguments, observation, and reason code.
- **A production-line path** — 3-frame stability, explicit OK/NG verdicts, ROI-aware counting, and a MES client. The line layer is off by default because counting and reporting have external side effects.
- **A reproducible benchmark** — 800 synthetic parts with exact labels and bounding boxes, generated from a fixed seed. The CLI regenerates the dataset, runs the detector and the full agent loop, and produces a dry-run AWS receipt.
- **An AWS evidence path** — S3 for annotated evidence, Lambda for batch aggregation, CloudWatch metrics, SNS review alerts, and one CloudFormation stack.
- **A virtual camera** — VirtualPath-Core renders a 3D inspection station without a window and serves it at `/cam1` as MJPEG. The vision app connects through the same Network Stream path it uses for a real camera.

![VirtualPath-Core as a virtual camera](https://raw.githubusercontent.com/Virtual-Path/virtual-path-vision/master/docs/images/12_engine_virtual_camera.png)

*The 3D engine renders the production line headless; the vision app consumes `/cam1` as an ordinary MJPEG stream.*

![Surface inspection in the desktop app](https://raw.githubusercontent.com/Virtual-Path/virtual-path-vision/master/docs/images/14_surface_camera.png)

*Surface Defect mode over a live MJPEG stream: raw frame on the left, detector overlay on the right.*

## How we built it

### A detector that survives the part edge

The detector starts with a 5×5 Gaussian blur and Otsu thresholding. The largest external contour becomes the part mask. Everything after that happens inside an eroded version of that mask.

The erosion is **17 px**, and that number is not cosmetic. Our first version used morphological opening to estimate the background and only a 6 px margin. It scored **52% precision**: 335 of 400 clean parts were flagged. Opening turns periodic brushed texture into a false “upper envelope”, and the narrow margin lets the part border through. A step edge produces a halo roughly three sigma wide, which is stronger than a real scratch.

We replaced the estimator with a Gaussian scale space and widened the exclusion to a 35×35 ellipse. Both effects disappeared. The regression test `EdgeMargin_DoesNotReportPartBorderAsDefect` injects the border failure and fails if any clean part is reported.

### Two scales, because one scale cannot see both

A single Gaussian cannot cover a 2 px scratch and a 35 px stain at the same time.

| Scale | Sigma | Sees | Why not the other scale |
|---|---:|---|---|
| Fine | 3 | 2 px scratches, 3–8 px spots | At sigma 12 a scratch becomes a 20 px ridge; its aspect ratio and IoU collapse |
| Coarse | 8 | 12–35 px low-contrast stains | At sigma 3 a stain's interior is flat; only its rim survives |

Each scale computes `|image - Gaussian(image)|` in both directions, thresholds inside the part mask, applies morphological cleanup, and classifies contours by aspect ratio, area, and length. The coarse pass only contributes stain candidates, and only when it does not overlap an existing fine-scale finding.

The score is the mean response inside the contour divided by a reference strength, clamped to 0–1. A part is NG when the dominant finding reaches 0.25. We deliberately did not tune a separate threshold per class: the same operating point is used in the benchmark and in the app, and the sensitivity sweep shows the precision/recall trade-off instead of hiding it.

### The agentic loop

The agent is a small, deterministic policy, not a prompt wrapper. It calls the detector, reads the observation, and decides whether the evidence is good enough to act on:

```text
inspect(frame, sensitivity=0.60)
  ├─ part not found ............ re-acquire once → else escalate
  ├─ focus < 10 ............... re-inspect with CLAHE → still low → escalate
  ├─ score ≥ 0.25 (NG)
  │    ├─ stain and score ≤ 0.50 ... quarantine + human review
  │    ├─ score ≥ 0.60 ............. reject
  │    └─ otherwise ................ re-inspect at 0.85 + CLAHE → decide
  ├─ score in [0.20, 0.25) .... re-inspect at 0.85 + CLAHE → decide
  └─ otherwise ................ pass
```

What makes it agentic is not the branching. It is that the visual observation changes the **next tool call's arguments**, and the change is recorded. A real trace for part P0401:

```json
{"partId":"P0401","action":"Reject","reasonCode":"confirmed_by_reinspection",
 "steps":[
  {"index":0,"tool":"inspect","args":{"sensitivity":0.6,"contrastBoost":false},
   "observation":"ng=True, class=Spot, score=0.41, findings=2"},
  {"index":1,"tool":"inspect","args":{"sensitivity":0.85,"contrastBoost":true},
   "observation":"ng=True, class=Stain, score=0.29, findings=1",
   "reasonCode":"ambiguous_defect"}
 ]}
```

The first pass was ambiguous, so the agent re-ran with different parameters. The second observation changed the class and still cleared the NG threshold, so the part was rejected with `confirmed_by_reinspection`. That is the difference between a detector and an agent: the detector reports; the agent decides what evidence it needs next.

Across the 800-part benchmark, **211 parts (26.4%) triggered a vision-driven re-inspection** before the final action. The final actions were 514 pass, 240 reject, 45 quarantine, and 1 escalate. Agreement with ground truth after the loop was 85.5%.

### From benchmark to operator console

The benchmark is not a separate model. **Surface Defect** calls the same `DefectDetector` as the CLI, so the measured code is the shipped code.

The desktop path adds the things a benchmark does not need:

- **ROI from the line card** — only the fixture area is inspected, and finding rectangles are translated back by the ROI origin before display and evidence rendering.
- **Explicit verdict** — the station uses a fixed tracking signature, `"surface"`, and the OK/NG conclusion travels as `Detection.Verdict`. The stability filter requires three agreeing frames.
- **Evidence on transition only** — an annotated PNG is written on `absent|unusable|ok → ng`, not every frame. At 30 fps, the naive version wrote 30 files per second.
- **Threading** — detection and evidence rendering run on the capture thread; UI updates are marshalled. Line settings come from an immutable snapshot refreshed on the UI thread, because reading WPF controls from the capture thread crashed the app.

### Evidence and cloud

Every part gets a machine-readable record: the image, the detector output, the agent steps, and the final action. The AWS path turns that into an evidence lake:

- **S3** stores annotated evidence and evaluation JSON, keyed by date.
- **Lambda** aggregates the batch and returns a summary.
- **CloudWatch** carries the operational metrics a plant already monitors.
- **SNS** notifies a quality owner when a part is quarantined.

The whole stack is one CloudFormation template. Inference stays at the edge; frames are not shipped to the cloud, and the inspection loop remains under 10 ms. Without AWS credentials the CLI automatically falls back to a dry-run receipt, so judges can run the full pipeline without an account. The live-account run is the remaining deployment step; we did not want to hide that behind a fake success.

### A 3D engine as a camera

To make the demo reproducible, we built **VirtualPath-Core**, an Avalonia + OpenGL ES 3.0 3D scene engine. It has a desktop editor with a scene tree, transform editing, glTF/GLB import, keyframe animation, and undo/redo. More importantly for this project, it has a headless camera bridge:

- a hidden GLFW context renders the production-line scene into an FBO;
- pixels are read back, flipped, and JPEG-encoded;
- a small TCP server serves `multipart/x-mixed-replace` at `/cam1`.

The vision app connects with the same Network Stream path as a real camera. No special frame source, no engine SDK, no mock. The 3D scene includes a conveyor, inspection station, warning lines, totes, good parts, a deliberately dark defective part, and same-colour decoy blocks on the back wall. The decoys matter: they make the detection problem non-trivial.

## Challenges we ran into

### The 52% precision start

The first detector was a useful failure. Morphological opening produced a false envelope on brushed metal, and a 6 px edge margin let the part border through. The result was 52% precision, with 335 clean parts flagged. Replacing the background estimator with a Gaussian scale space and widening the exclusion to 17 px removed both failure modes. We kept the regression test because the bug is easy to reintroduce.

### When re-inspection makes it worse

Two true scratches were cleared by the agent's second pass at sensitivity 0.85. The higher sensitivity lowers the fine threshold, but the 5×5 morphological close can merge a faint scratch into the surrounding texture and reduce its mean response. The part is then passed with confidence. This is the most dangerous failure in the system, and it is in the report on purpose.

The fix we want next is evidence-preserving re-inspection: keep the first-pass score in the trace and require a minimum absolute drop before clearing. Today the second pass alone decides. We would rather show the failure than hide it.

### The app that would not close

Closing the app with a camera open could hang forever. `StopCapture()` re-acquired a lock after waiting for the capture loop, while the loop's `finally` held the same lock across a `Dispatcher.Invoke` — each side waiting for the other, and the dispatcher could only be pumped by the UI thread that was blocked. Whichever side won the lock decided whether closing worked, which is why it was intermittent.

The fix was to stop touching the lock after the wait, set `IsStopping` before the wait, move native handle disposal out of the live capture loop, and replace blocking `Dispatcher.Invoke` calls with non-blocking dispatches plus in-flight de-duplication. We added a deterministic regression test that holds the lock and proves `StopCapture` no longer waits on it. The measured result was 0 hangs in 8 runs; before, it was roughly 1 in 5.

### The camera path that was not `/video`

The engine serves `/cam1`. The vision app had `/video` hardcoded. Every connection got a 404, OpenCV reopened, and the engine logged an endless stream of “client disconnected” messages that looked like a network problem. The fix was a configurable stream path, but the better fix was observability: the engine now logs `rejected /video (only /cam1 is served)`, so the next person sees the actual mistake instead of a misleading disconnect. After the fix, the stream delivered 30/30 frames at 1280×720.

## Accomplishments that we're proud of

On the 800-part benchmark at sensitivity 0.60, single-threaded:

| Metric | Result |
|---|---:|
| Precision | **100.00%** (0 false alarms in 400 good parts) |
| Recall | **82.50%** |
| F1 | **90.41%** |
| Image-level accuracy | **91.25%** |
| Defect-class accuracy (within true positives) | **86.67%** |
| Localization mean IoU | **0.613** |
| Latency p50 / p95 | **7.88 ms / 8.79 ms** |
| Throughput | **128.2 FPS** at 640×480 |

Other results we are proud of:

- **211/800 parts (26.4%)** had a vision-driven re-inspection before the final action.
- **117 assertions** pass across production-line and inspection tests.
- The solution builds with **0 warnings and 0 errors**.
- VirtualPath-Core builds with **0 warnings and 0 errors** and passes **19/19 xUnit tests**.
- The desktop path was exercised end to end over MJPEG: P0400 produced `NG Scratch score=0.56` and an evidence PNG. P0401 was misclassified as a spot (`NG Spot 0.38` after MJPEG re-encoding; `0.41` in the PNG trace) and is documented as a failure, not retouched.
- The virtual camera delivered 30/30 frames at 1280×720.

## What we learned

- **Agentic vision is a control policy, not a prompt.** The useful part is the changed next action, the guardrail, and the trace. A model that only produces a better label is still a detector.
- **Measure the failure boundary, not just the headline.** A 100% precision / 82.5% recall operating point is more useful than a single accuracy number, especially when the failures are categorized.
- **Reproducibility is a feature.** A fixed seed, pinned dependencies, a one-command benchmark, and committed result artifacts make the claims inspectable.
- **Systems fail at the seams.** The hardest bugs were not in the detector; they were a UI/capture deadlock, a wrong URL path, and an AWS path that could not be tested without credentials.
- **Keep the failures visible.** The two scratches cleared by re-inspection and the P0401 class confusion are the most valuable pages in the report because they define what to fix next.

**What is not claimed yet:** the 800-part benchmark is synthetic, defects within 17 px of the part edge are not inspected, the AWS path is dry-run verified but not live-account verified, and the MES/PLC drivers are implemented against their contracts but not commissioned on real hardware.

## What's next for Virtual Path Vision

1. Run the benchmark on a real line camera and measure the synthetic-to-real gap.
2. Add a small U-Net for low-contrast stains, with the classical detector as a deterministic fallback.
3. Make re-inspection evidence-preserving: keep both passes and require an explicit reason to clear a finding.
4. Deploy the AWS stack to a real account and stream agent traces to Athena so quality engineers can query “every part cleared by re-inspection”.
5. Re-verify the physical USB camera path; the current demonstrated acquisition path is the VirtualPath-Core MJPEG camera.

The code, technical report, and recorded artifacts are public:

- [VirtualPath Vision](https://github.com/Virtual-Path/virtual-path-vision)
- [VirtualPath Core](https://github.com/Virtual-Path/virtual-path-core)
- [Technical report](https://github.com/Virtual-Path/virtual-path-vision/blob/master/docs/competition/TECHNICAL_REPORT.md)
