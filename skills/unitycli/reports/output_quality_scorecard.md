# UnityCLI Output Quality Scorecard

Release: 0.1.10
Status: governed baseline
Captured: 2026-08-06

## Output Contract

Normal task responses have no additional fixed format. A feedback summary is appended only when `FeedbackMode` is on, a real friction event occurred, and the recorder returned `ok:true`. With feedback off or no friction, there is no feedback record and no feedback summary.

The feedback output contract requires category, concise issue summary, optional workaround, `recordId`, and local path. It excludes raw prompt, complete CLI I/O, source, logs, paths supplied as content, URLs, endpoints, and secrets. Recorder failure is reported as feedback-not-written and does not replace the task result.

## Scorecard

| Dimension | Result | Evidence | Residual |
|---|---:|---|---|
| Initial context budget | PASS: 963 / 1300 (Skill body: 691) | `resource-boundary.json` | Token estimator is heuristic |
| Broad Unity trigger fixture | PASS: 29 / 29 | `trigger-eval.json` | Deterministic semantic fixture, not production telemetry |
| Runtime output assertions | PASS: 5 / 5 | `output-eval.json` | Recorded fixtures, not provider execution |
| Baseline vs with-Skill delta | +100 points | 0% to 100% assertion pass rate | Baseline is a controlled comparison |
| File-backed fixture coverage | PASS: 2 cases | `evals/output/fixtures/feedback-scenario.json` | More real-world fixtures may be added from feedback |
| Caller timeout authority | PASS | Runtime tests and clean Agent review | Provider behavior beyond Skill text is not measured |
| Feedback off silence | PASS | Runtime tests and output eval | Production adoption telemetry unavailable |
| Feedback privacy negatives | PASS | Secret/endpoint/path negative matrix | Novel secret formats can still exist |
| Concurrent JSONL validity | PASS | 16 parallel Windows PowerShell writers | Cross-session Windows desktop contention not measured |
| Rolling bound | PASS | Main + 3 archives, each <= 64 KiB | Longitudinal retention utility not measured |
| Installed bundle reachability | PASS | Public workspace/user install, drifted-update, check, and recorder smoke for `.agents` and Claude | Verified with isolated filesystem targets |
| Clean Agent interpretation | PASS | Black-box runtime discovery transcript and main-agent audit | Human adjudication unavailable |

## Rollback Boundary

The runtime bundle rolls back to one fixed previous projection when publish/readback fails. `FeedbackMode` is preserved across updates. Feedback records are append-only and bounded rather than transactionally rolled back. Output evaluation artifacts do not enter the installed runtime bundle.

## Failure Taxonomy

Current file-backed output gate has no with-Skill assertion failures. Future failures are classified as:

- `caller_authority`: Skill invents a caller-side retry or mutation restriction.
- `off_mode_noise`: feedback is written without opt-in friction.
- `privacy_leak`: feedback includes prohibited raw content.
- `missing_recorder`: feedback summary is claimed without a successful local record.
- `unnecessary_wrapper`: pure file work is routed through Unity.
- `missing_editor_route`: live Editor proof does not use the connected catalog.
- `missing_input_files`: governed evaluation lacks a file-backed fixture.

## Missing Evidence

The following are **missing evidence**:

- Provider-backed output eval across OpenAI, Claude, and generic agents.
- Independent blind human review and adjudication.
- Native-client telemetry and long-term user feedback quality.
- Production false-positive/false-negative routing rates.
- Approval from an external release reviewer.

No missing metric is treated as PASS.
