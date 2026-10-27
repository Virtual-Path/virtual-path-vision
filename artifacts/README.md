# Competition artifacts

Deterministic outputs from the 800-part synthetic benchmark and the agentic re-inspection loop. Regenerate with:

```powershell
dotnet run --project VirtualPathVision.Competition -- all
```

## Results

| File | Content |
|---|---|
| `evaluation.json` | Full metrics, per-class results and failure list |
| `evaluation.md` | Human-readable metric tables |
| `sweep.json` / `sweep.md` | Sensitivity sweep |
| `agent-summary.json` | Final action counts and ground-truth agreement |
| `agent-trace.jsonl` | One JSONL record per part; every tool call with arguments, observation and reason code |
| `actuator.log` | Deterministic pass/reject/quarantine/escalate actuator trace |
| `aws-receipt.json` | Dry-run AWS receipt; no live deployment |
| `evidence/` | 12 annotated pass/reject/quarantine/escalate evidence images |
| `probe-*.jpg` | Probe frames used to reproduce individual findings |

## Headline metrics

- Precision **100.00%**, Recall **82.50%**, F1 **90.41%**, image accuracy **91.25%**
- Defect-class accuracy **86.67%**, mean IoU **0.613**
- p50 **7.88 ms**, p95 **8.79 ms**, **128.2 FPS**
- **211/800** parts had a vision-driven re-inspection; **85.5%** agreement with ground truth
- Final actions: 514 pass, 240 reject, 45 quarantine, 1 escalate

## Scope

- The benchmark is **synthetic**, generated from a fixed seed.
- The detector excludes a **17 px** border around the part; defects there are not inspected.
- AWS is a **dry-run receipt only**. The CloudFormation template is in `infra/template.yaml`; live deployment is not claimed.
- The physical USB/MSMF camera path and MES/PLC integration are not commissioned in this demo.

See `docs/competition/TECHNICAL_REPORT.md` for the full design and failure analysis.
