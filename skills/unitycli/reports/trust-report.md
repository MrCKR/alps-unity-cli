# UnityCLI Skill Trust Report

Status: governed local package
Owner: alps-unity-cli maintainers
Review cadence: per-release
Reviewed: 2026-10-22 for release 0.9.0

## Scope

This trust report covers the source Skill package, the installed runtime projection (`SKILL.md`, `references/**`, `scripts/**`), and `scripts/record-feedback.ps1`. Governed assets under `agents/**`, `evals/**`, `examples/**`, `security/**`, `reports/**`, and `manifest.json` are not installed into Agent runtime folders.

## Permission Surface

| Capability | Decision | Scope | Evidence |
|---|---|---|---|
| Network | deny | Runtime Skill and recorder | `security/network_policy.json`; static network API scan has zero matches |
| File read | allow | Installed Skill files and public `unitycli` output | Runtime contract |
| File write | approved by owner | Installed Skill `experience/feedback.jsonl` plus three archives; updated by the Skill's own script | D008; recorder tests |
| Subprocess | approved by owner | Bundled `record-feedback.ps1` through Windows PowerShell | D008; runtime Skill |
| Remote inline execution | forbid | All adapters | `agents/interface.yaml` |

The recorder accepts bounded summary fields, rejects common credential, endpoint, URL, and path forms, requires the Skill root to resolve to a fixed local drive with no existing reparse path component and to contain `SKILL.md`, writes UTF-8 JSONL, and uses a Windows named mutex. Each persistent file is limited to 64 KiB; one main file and three archives are the fixed maximum. It does not upload feedback. Records live in the installed Skill's `experience/` directory, which is a state area outside the runtime projection: it never affects projection equality or the install integrity check, and a Skill update keeps it.

## Caller Authority

The Skill does not add confirmation, intent review, or a predefined operation range. `E_EXECUTION_TIMEOUT`, `transient`, and `nextActions` communicate facts or suggestions; the caller retains authority after observing business state. Same-call recovery may continue exact bytes only while `editorSessionId` is unchanged; after the first mutation POST, a new Editor session forbids automatic redelivery.

## Rollback Boundary

- Skill installation atomically commits one fixed previous runtime projection before update writes, and retains it until the new complete bundle passes readback.
- A legal installed `FeedbackMode` value is preserved during updates.
- The installed Skill's `experience/` state directory survives an update, because an update replaces only projected files.
- Feedback JSONL is append-only and not transactionally rolled back; rotation bounds retention.
- Recorder failure does not change the Unity task result.

## Verified Evidence

Verified for release 0.9.0 (2026-10-22):

- Full repository suite: 1392/1392 PASS.
- Skill governance and projection: the installer projects byte-identical runtime bundles for both hosts; the installed Skill keeps its `experience/` records across an update and still passes the integrity check.
- Recorder concurrency, rotation, privacy negatives, PowerShell 5.1 BOM, non-ASCII Skill path, missing `SKILL.md` refusal, and reparse-point refusal: PASS.
- Current package facts: `SKILL.md` 106 lines / 5,813 characters / 10,625 UTF-8 bytes; references 5; recorded output cases 7; trigger classification cases 33.

Recorded by the 2026-08-05 audit (not re-run for 0.7.0):

- Full repository suite at that time: 876/876 PASS; targeted Skill/installer/docs regression: 61/61 PASS.
- Resource boundary: limit 1300 estimated initial-load tokens; the audit measured 963 (Skill body 691) while `references` held four files. Its estimator is not part of this repository, so the figure is not a live value.
- Trigger fixture gate: 29/29 classifications on the fixture as it stood then.
- Output assertion gate: 5/5 with-skill cases on the cases as they stood then; executing the assertions needs the external evaluator, which this repository does not ship.
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
