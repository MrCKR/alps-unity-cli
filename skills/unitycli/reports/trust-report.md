# UnityCLI Skill Trust Report

Status: governed local package
Owner: alps-unity-cli maintainers
Review cadence: per-release
Reviewed: 2026-08-05

## Scope

This trust report covers the source Skill package, the installed runtime projection (`SKILL.md`, `references/**`, `scripts/**`), and `scripts/record-feedback.ps1`. Governed assets under `agents/**`, `evals/**`, `examples/**`, `security/**`, `reports/**`, and `manifest.json` are not installed into Agent runtime folders.

## Permission Surface

| Capability | Decision | Scope | Evidence |
|---|---|---|---|
| Network | deny | Runtime Skill and recorder | `security/network_policy.json`; static network API scan has zero matches |
| File read | allow | Installed Skill files and public `unitycli` output | Runtime contract |
| File write | approved by owner | `%LOCALAPPDATA%\UnityCli\feedback\feedback.jsonl` plus three archives | D008; recorder tests |
| Subprocess | approved by owner | Bundled `record-feedback.ps1` through Windows PowerShell | D008; runtime Skill |
| Remote inline execution | forbid | All adapters | `agents/interface.yaml` |

The recorder accepts bounded summary fields, rejects common credential, endpoint, URL, and path forms, requires `LOCALAPPDATA` to resolve to a fixed local drive with no existing reparse path component, writes UTF-8 JSONL, and uses a Windows named mutex. Each persistent file is limited to 64 KiB; one main file and three archives are the fixed maximum. It does not upload feedback.

## Caller Authority

The Skill does not add confirmation, intent review, or a predefined operation range. `E_EXECUTION_TIMEOUT`, `transient`, and `nextActions` communicate facts or suggestions; the caller retains authority after observing business state. Same-call recovery may continue exact bytes only while `editorSessionId` is unchanged; after the first mutation POST, a new Editor session forbids automatic redelivery.

## Rollback Boundary

- Skill installation atomically commits one fixed previous runtime projection before update writes, and retains it until the new complete bundle passes readback.
- A legal installed `FeedbackMode` value is preserved during updates.
- Feedback JSONL is append-only and not transactionally rolled back; rotation bounds retention.
- Recorder failure does not change the Unity task result.

## Verified Evidence

- Full repository tests: 876/876 PASS; targeted Skill/installer/docs regression: 61/61 PASS.
- Recorder concurrency, rotation, privacy negatives, PowerShell 5.1 BOM, and non-ASCII stdout: PASS.
- Public runtime install, drifted update, check, marker preservation, and installed-recorder smoke across `.pi`, `.claude`, and `.agents`: PASS.
- Resource boundary: 963 estimated initial-load tokens (691 Skill body tokens), limit 1300.
- Trigger fixture gate: 29/29 classifications.
- Output assertion gate: 5/5 with-skill cases, 100% pass rate.
- Dev Unity compile: 0 errors, 0 warnings; Editor returned live/ready/edit.
- Clean Agent runtime discovery and read-only business verification: PASS.

## Scanner Limits And Missing Evidence

The current Yao `trust_check.py` inventories Python scripts only; it does not identify the PowerShell recorder. Therefore its generated script count is not accepted as proof for this recorder. PowerShell trust is established by repository tests, source scan, local execution, and the policies above.

The following remain **missing evidence** and must not be presented as verified:

- A cross-process read lease for an Agent that read an old `SKILL.md` and then requests a source-deleted reference during a concurrent update; the installer guarantees target continuity and entrypoint-last publication, not a multi-file snapshot for already-started readers.
- OS-native sandbox enforcement of the declared file-write and network permissions.
- Independent human release approval or blind-review adjudication.
- Production telemetry for trigger precision, feedback usefulness, or false-positive privacy rejection.
- Longitudinal retention and multi-session contention measurements.
- Provider-specific native permission enforcement across OpenAI, Claude, generic Agent Skills, and VS Code.
