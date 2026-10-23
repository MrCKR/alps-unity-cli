import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const repoRoot = process.cwd();
const unitypkg = new URL("../unitypkg/", import.meta.url);
const runToolsRoot = new URL("Editor/Tools/debug/run/", unitypkg);
const debugRoot = new URL("Editor/Infrastructure/Debug/", unitypkg);
const sharedRoot = new URL("Shared/", unitypkg);

/** 读取 unitypkg 内的生产源码。 */
async function source(root: URL, path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

const TOOLS = ["begin", "end", "wait", "info", "list", "compare"];
const READERS = ["wait", "info", "list", "compare"];

test("the five debug.run tools exist with the declared descriptor shape", async () => {
  for (const tool of TOOLS) {
    const text = await source(runToolsRoot, `../run-${tool}.cs`);
    assert.match(text, /IUnityCliTool<DebugRun\w+Request, DebugRun\w+Result>/, `${tool}.cs must implement the typed Tool interface`);
    assert.match(text, new RegExp(`Name = "debug\\.run-${tool}"`), `${tool}.cs must declare its exact public name`);
    assert.match(text, /Group = "debug"/);
    assert.match(text, /Target = "editor"/);
    assert.match(text, /PlayModeSupport = "supported"/);
    assert.match(text, /DestructiveHint = false/, `${tool}.cs declares no destructive hint`);
    assert.match(text, /DebugLimits\.RunSummary\(\)/, `${tool}.cs must publish the single run limit summary`);
    assert.doesNotMatch(text, /EditorApplication\.update \+=/, "the run domain must not register its own update callback");
    assert.doesNotMatch(text, /MaxRunSummariesPerSession = |MaxDetailedRunWindowsPerSession = |MaxDetailedRunBytesGlobal = /, `${tool}.cs must not restate a run limit`);
  }
  // begin 是唯一跨效果屏障并声明副作用的 run Tool；其余是纯读取。
  assert.match(await source(runToolsRoot, "../run-begin.cs"), /SideEffects = new\[\] \{ "debug-session" \}/);
  assert.match(await source(runToolsRoot, "../run-begin.cs"), /ctx\.CommitEffect\(\);/);
  for (const reader of READERS) {
    const text = await source(runToolsRoot, `../run-${reader}.cs`);
    assert.doesNotMatch(text, /SideEffects =/, `${reader}.cs is a pure read and must declare no side effect`);
    assert.doesNotMatch(text, /ctx\.CommitEffect\(\)/, `${reader}.cs must not cross an effect barrier`);
  }
  const end = await source(runToolsRoot, "../run-end.cs");
  assert.doesNotMatch(end, /SideEffects =/, "debug.run-end seals evidence and declares no side effect");
  assert.doesNotMatch(end, /ctx\.CommitEffect\(\)/, "debug.run-end must not cross an effect barrier");
});

test("the run domain declares every budget in the single debug limit owner", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of [
    "MaxRunSummariesPerSession = 16",
    "MaxDetailedRunWindowsPerSession = 4",
    "MaxDetailedRunBytesGlobal = 32L * 1024 * 1024",
    "MaxRunRequiredChecks = 32",
    "MaxRunCheckSources = 4",
    "MaxRunSourceRequirements = 16",
    "MaxRunEndConditions = 8",
    "MaxRunWindowMs = 3600000L",
    "MaxRunEvidencePerCheck = 8",
    "MaxRunCoverageGaps = 32",
    "MaxRunSegments = 16",
    "RunWaitMaxMs = 30000",
    "RunListDefaultLimit = 8",
    "RunListMaxLimit = 16",
  ]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  assert.match(limits, /internal static string RunSummary\(\)/);
  // 单个窗口的详细证据预留不得独占全局详细证据账本。
  assert.match(limits, /MaxRunDetailBytesPerWindow = \(long\)MaxRunRequiredChecks \* MaxRunEvidencePerCheck \* MaxRunEvidenceRecordBytes/);
});

test("the run owner reuses the single event store, scheduler and session owner", async () => {
  const host = `${await source(debugRoot, "DebugRunHost.cs")}${await source(debugRoot, "DebugRunEvidence.cs")}`;
  for (const reuse of [
    "DebugEventStore.CaptureBoundary()",
    "DebugEventStore.Range(",
    "DebugEventStore.Gaps",
    "DebugEventStore.AppendFailures",
    "DebugScheduler.Describe()",
    "DebugSessionHost.Info(",
    "DebugSessionHost.RuntimeId()",
    "DebugSessionHost.DomainGeneration()",
    "MethodHookHost.List(",
    "DebugProgramHost.List(",
  ]) {
    assert.ok(host.includes(reuse), `the run owner must reuse ${reuse}`);
  }
  for (const forbidden of [
    "Application.logMessageReceived",
    "IUnityCliMethodHookBackend",
    "Harmony",
    "new Thread(",
    "new Task(",
    "EditorApplication.update +=",
    "DebugSessionStore",
    "MethodHookStore",
    "MaxRunSummariesPerSession = ",
  ]) {
    assert.ok(!host.includes(forbidden), `the run owner must not use ${forbidden}`);
  }
  // 事件真值只有一份：run owner 不建立第二个事件环或第二条投递队列。
  assert.doesNotMatch(host, /class UnityCliDebugEventBuffer/, "the run owner must not define a second event ring");
  assert.doesNotMatch(host, /EnqueueDelivery|TryClaimDelivery|PlantDriver|CloseDriver/, "the run owner must not build a second delivery queue or driver owner");
});

test("the event store exposes one atomic capture boundary and a non-consuming range read", async () => {
  const store = await source(debugRoot, "DebugEventStore.cs");
  assert.match(store, /internal static UnityCliDebugEventBoundary CaptureBoundary\(\)/);
  assert.match(store, /internal static List<UnityCliDebugEvent> Range\(long afterExclusive, long upToInclusive\)/);
  const buffer = await source(sharedRoot, "UnityCliDebugEvents.cs");
  assert.match(buffer, /public UnityCliDebugEventBoundary CaptureBoundary\(\)[\s\S]*?lock \(gate\)/, "the boundary capture must be one critical section");
  assert.match(buffer, /lock \(gate\)[\s\S]*?Sequence = sequence,[\s\S]*?Dropped = dropped,/, "the boundary capture must read the sequence and drop counters together");
  assert.match(buffer, /public List<UnityCliDebugEvent> Range\(long afterExclusive, long upToInclusive\)[\s\S]*?lock \(gate\)/, "the range read must be a locked snapshot");
});

test("an assertion failure is never expressed as an error code", async () => {
  const files = [
    "src/output.ts",
    "docs/error-codes.md",
    `${await source(runToolsRoot, "../run-begin.cs")}${await source(runToolsRoot, "../run-end.cs")}${await source(runToolsRoot, "../run-wait.cs")}${await source(runToolsRoot, "../run-info.cs")}`,
    await source(debugRoot, "DebugRunHost.cs"),
  ];
  assert.ok(!files.join("\n").includes("E_DEBUG_ASSERTION_FAILED"), "no assertion-failed error code may exist");
  assert.match(await readFile(join(repoRoot, "src/output.ts"), "utf8"), /E_DEBUG_RUN_NOT_FOUND: "E_DEBUG_RUN_NOT_FOUND"/);
  assert.match(await readFile(join(repoRoot, "docs/error-codes.md"), "utf8"), /`E_DEBUG_RUN_NOT_FOUND`/);
  const runDtos = await source(runToolsRoot, "DebugRunDtos.cs");
  assert.match(runDtos, /public string Verdict \{ get; set; \}/, "the report carries the verdict as data");
  // 判定只能来自 DebugRunEvidence 的一次评估，宿主不重新解释证据。
  const host = await source(debugRoot, "DebugRunHost.cs");
  assert.match(host, /var verdict = DebugRunEvidence\.Evaluate\(/, "the report assembly must read one evaluation");
  assert.doesNotMatch(host, /new ErrorCode\(|ErrorCode\./, "the host maps no verdict into an error code");
});

test("the run vocabulary freezes the verdict, quantifier and reason codes", async () => {
  const shared = await source(sharedRoot, "UnityCliDebugRuns.cs");
  for (const token of [
    'public const string Passed = "passed"',
    'public const string Failed = "failed"',
    'public const string Indeterminate = "indeterminate"',
    'public const string Once = "once"',
    'public const string Every = "every"',
    'public const string WholeWindow = "whole-window"',
    'public const string Absent = "absent"',
    'public const string NoEvidence = "no-evidence"',
    'public const string CoverageGap = "coverage-gap"',
    'public const string SampledOnly = "sampled-only"',
    'public const string EvidenceTruncated = "evidence-truncated"',
    'public const string WindowNotComplete = "window-not-complete"',
    'public const string WindowInterrupted = "window-interrupted"',
    'public const string ConfigurationChanged = "configuration-changed"',
    'public const string ConfigurationBoundaryMissing = "configuration-boundary-missing"',
    'public const string TooManySegments = "too-many-segments"',
    'public const string QueueNotDrained = "queue-not-drained"',
    'public const string DetailEvicted = "detail-evicted"',
    'public const string CoverageNotRequired = "coverage-not-required"',
    'public const string Counterexample = "counterexample"',
  ]) {
    assert.ok(shared.includes(token), `UnityCliDebugRuns must declare ${token}`);
  }
  for (const state of ['public const string Open = "open"', 'public const string Sealed = "sealed"', 'public const string Interrupted = "interrupted"']) {
    assert.ok(shared.includes(state), `UnityCliDebugRuns must declare the window state ${state}`);
  }
});

test("the evidence kernel requires coverage proof for negative and whole-window checks", async () => {
  const evidence = await source(debugRoot, "DebugRunEvidence.cs");
  assert.match(evidence, /requiresWholeWindow/, "the kernel must distinguish whole-window and absent quantifiers");
  assert.match(evidence, /UnityCliDebugRunVerdictReasons\.CoverageNotRequired/, "a missing coverage declaration must be reported, not assumed");
  assert.match(evidence, /intervalGaps\.Count == 0/, "proven coverage requires an interval without unreadable gaps");
  assert.match(evidence, /entry\.Sampled \|\| entry\.SampleEvery > 1/, "a sampled stream only proves sample points");
  assert.match(evidence, /entry\.Truncated \|\| entry\.ProjectionTruncated/, "truncated evidence cannot prove a check");
  assert.match(evidence, /FailedCount > 0L[\s\S]*?Status = StatusCounterexample/, "a failed match is a counterexample before coverage is considered");
  const host = await source(debugRoot, "DebugRunHost.cs");
  assert.match(host, /ValidateCheckCoverage\(checks, requirements\)/, "begin must reject a whole-window or absent check without a declared coverage requirement");
  assert.match(host, /isDeclaredOnly|DeclaredOnly = string\.Equals\(condition\.Kind, UnityCliDebugRunEndConditionKinds\.Signal/, "a signal condition is reported as declared-only");
  assert.match(host, /QueueDrained/, "the capture must report whether the scheduler queue was drained");
});

test("coverage proof is consumed only by the quantifiers the descriptor names", async () => {
  const evidence = await source(debugRoot, "DebugRunEvidence.cs");
  const host = await source(debugRoot, "DebugRunHost.cs");
  const begin = await source(runToolsRoot, "../run-begin.cs");
  const end = await source(runToolsRoot, "../run-end.cs");
  const info = await source(runToolsRoot, "../run-info.cs");
  const shared = await source(sharedRoot, "UnityCliDebugRuns.cs");
  const dtos = await source(runToolsRoot, "DebugRunDtos.cs");
  // 判定内核只在消费覆盖的量词缺少申报时给出 coverage-not-required；来源评估不再把它当判定原因。
  const sourceLoop = evidence.slice(evidence.indexOf("var requiredSources = RequiredSourcesImpl(window);"), evidence.indexOf("foreach (var check in window.Checks)"));
  assert.ok(sourceLoop.length > 0, "the source evaluation loop must exist");
  assert.ok(!sourceLoop.includes("CoverageNotRequired"), "a source evaluation must not publish coverage-not-required as a verdict reason");
  assert.match(evidence, /if \(requiresWholeWindow && !coverageDeclared\)[\s\S]*?CoverageNotRequired/, "coverage-not-required must be scoped to the quantifiers that consume coverage");
  assert.match(evidence, /evaluation\.CoverageRequired = requiresWholeWindow;/, "a check evaluation must expose whether its quantifier consumes coverage");
  // 两级报告各给出两个独立事实：要求了什么与证明了什么。
  assert.match(dtos, /public bool CoverageRequired \{ get; set; \}/, "the per-check result must expose coverageRequired next to coverageProven");
  assert.match(shared, /public const string None = "none"/, "the source proof vocabulary must name the no-declaration case");
  assert.match(host, /UnityCliDebugRunCoverageProofs\.None/, "a source without any declaration must report none instead of declared");
  // descriptor 说明每个量词是否需要申报，并使示例本身能得到 passed。
  assert.match(begin, /only whole-window and absent consume sourceRequirements/, "begin must state which quantifiers consume sourceRequirements");
  assert.match(begin, /once and every are decided by a retained matching event and are never rejected, never downgraded and never made indeterminate for omitting sourceRequirements/, "begin must state that once and every need no declaration");
  assert.match(begin, /DebugRunEndConditionSpec \{ Kind = "manual" \}/, "the once example must declare how the window ends");
  assert.match(begin, /Install that program before begin/, "the once example must state the configuration-segment precondition");
  for (const text of [end, info]) {
    assert.doesNotMatch(text, /passed requires every required check satisfied with proven coverage for the whole applicable window/, "the verdict rule must be stated per quantifier, not per window");
  }
});

test("the run tools are registered in the generated command contract", async () => {
  const contract = JSON.parse(await readFile(join(repoRoot, "contracts", "command-contract.json"), "utf8")) as { commands: Array<{ bridgeCommand: string; group: string }> };
  for (const tool of TOOLS) {
    const name = `debug.run-${tool}`;
    assert.ok(contract.commands.some((entry) => entry.bridgeCommand === name), `${name} must be in the generated command contract`);
  }
  const completion = JSON.parse(await readFile(join(repoRoot, "contracts", "tool-completion-contract.json"), "utf8")) as { tools: Array<{ toolName: string; executionClass: string }> };
  for (const tool of TOOLS) {
    assert.ok(completion.tools.some((entry) => entry.toolName === `debug.run-${tool}`), `debug.run-${tool} needs one completion contract`);
  }
  const compare = completion.tools.find((entry) => entry.toolName === "debug.run-compare");
  assert.equal(compare?.executionClass, "observed-state", "compare reads observed evidence state");
});

test("a sealed window retains its bounded evidence and releases only the payload", async () => {
  const host = await source(debugRoot, "DebugRunHost.cs");
  assert.match(host, /window\.FrozenReport = report/, "a terminal window freezes the report it closes with");
  assert.match(host, /if \(window\.FrozenReport == null\) window\.FrozenReport = report;/, "the first terminal report wins and is never rebuilt");
  assert.match(host, /ReleaseFrozenPayload\(window\.FrozenReport\);/, "eviction releases the frozen payload");
  assert.match(host, /frozen\.FirstCounterexample = null;/, "the released payload drops the counterexample values");
  assert.doesNotMatch(host, /frozen\.Verdict = /, "a released payload never rewrites the frozen verdict");
  const evidence = await source(debugRoot, "DebugRunEvidence.cs");
  assert.match(evidence, /internal DebugRunReportView FrozenReport;/, "the window record owns the frozen report");
});

test("compare requires every caller-declared dimension and assumes no alignment", async () => {
  const tool = await source(runToolsRoot, "../run-compare.cs");
  const dtos = await source(runToolsRoot, "DebugRunDtos.cs");
  const kernel = await source(debugRoot, "DebugRunCompare.cs");
  assert.match(dtos, /\[UnityCliRequired\] public DebugRunCompareAlignmentSpec Alignment \{ get; set; \}/, "alignment is a required input");
  for (const field of ["LeftRunId", "RightRunId", "Position", "ValuePath"]) {
    assert.ok(dtos.includes(`[UnityCliRequired] public string ${field} { get; set; }`), `${field} must be required`);
  }
  assert.ok(dtos.includes('[UnityCliRequired, UnityCliStringEnum("program", "hook", "log", "trigger", "pause")] public string Source { get; set; }'), "the source must be required and constrained");
  assert.ok(dtos.includes('[UnityCliRequired, UnityCliStringEnum("monotonic", "editor-tick", "unity-frame", "project-logical")] public string Clock { get; set; }'), "the clock must be required and constrained");
  assert.match(kernel, /an explicit alignment object, because this Tool never assumes a default alignment/, "a missing alignment must be rejected, not defaulted");
  assert.match(kernel, /UnityCliDebugClocks\.IsDefined\(request\.Clock\)/, "the clock must come from the declared clock list");
  assert.match(kernel, /string\.Equals\(plan\.AlignmentKind, UnityCliDebugRunCompareAlignments\.Key/, "key alignment must demand its key path");
  assert.match(kernel, /"alignment\.keyPath", "no key path for alignment kind "/, "ordinal alignment must reject a key path");
  assert.match(tool, /nothing is assumed for you|Nothing is assumed for you/, "the descriptor states that nothing is assumed");
  assert.match(tool, /never falls back to global sequence numbers, object addresses or wall time/, "the descriptor rules out the default alignments");
});

test("compare reads the one event store and the published event projection", async () => {
  const kernel = await source(debugRoot, "DebugRunCompare.cs");
  const host = await source(debugRoot, "DebugRunHost.cs");
  for (const reuse of ["DebugEventStore.CaptureBoundary()", "DebugEventStore.Range(", "DebugEventStore.Gaps", "DebugEventStore.AppendFailures"]) {
    assert.ok(host.includes(reuse), `the compare path must reuse ${reuse}`);
  }
  assert.match(kernel, /UnityCliDebugEventBuffer\.ToData\(entry\)/, "the compared fields come from the one published event projection");
  for (const forbidden of ["EditorApplication.update +=", "new Thread(", "new Task(", "Application.logMessageReceived", "class UnityCliDebugEventBuffer", "EnqueueDelivery", "TryClaimDelivery"]) {
    assert.ok(!kernel.includes(forbidden), `the compare kernel must not use ${forbidden}`);
  }
  assert.doesNotMatch(kernel, /debug\.run-compare[\s\S]{0,200}CommitEffect/, "compare is a pure read and crosses no effect barrier");
});

test("only a proven difference decides divergence and absence alone never does", async () => {
  const kernel = await source(debugRoot, "DebugRunCompare.cs");
  assert.match(kernel, /Provable = left\.Complete && right\.Complete/, "a difference that depends on an absent event needs two complete intervals");
  assert.match(kernel, /ProvablePair\(plan, left, right/, "a paired value difference needs provable alignment conditions");
  assert.match(kernel, /if \(!left\.AlignmentSound \|\| !right\.AlignmentSound\) return false;/, "key alignment must be sound on both sides");
  assert.match(kernel, /UnityCliDebugRunCompareReasons\.CountMismatchUnproven/, "an unprovable count difference is reported as such");
  assert.match(kernel, /UnityCliDebugRunCompareReasons\.WindowOpen/, "an open window cannot support equal or diverged");
  assert.match(kernel, /Trustworthy = false/, "an incomparable value cannot become a difference");
  const shared = await source(sharedRoot, "UnityCliDebugRuns.cs");
  for (const token of [
    'public const string Equal = "equal"',
    'public const string Diverged = "diverged"',
    'public const string Indeterminate = "indeterminate"',
    'public const string Ordinal = "ordinal"',
    'public const string Key = "key"',
    'public const string EventMissingRight = "event-missing-right"',
    'public const string EventMissingLeft = "event-missing-left"',
    'public const string ValueMissing = "value-missing"',
    'public const string WindowOpen = "window-open"',
    'public const string ClockUnavailable = "clock-unavailable"',
    'public const string AlignmentKeyMissing = "alignment-key-missing"',
    'public const string AlignmentAmbiguous = "alignment-ambiguous"',
    'public const string ValueDifferenceUnproven = "value-difference-unproven"',
  ]) {
    assert.ok(shared.includes(token), `UnityCliDebugRuns must declare ${token}`);
  }
  for (const state of ['public const string Live = "live"', 'public const string Frozen = "frozen"', 'public const string Evicted = "evicted"']) {
    assert.ok(shared.includes(state), `UnityCliDebugRuns must declare the evidence state ${state}`);
  }
});

test("the compare bounds live only in the single debug limit owner", async () => {
  const limits = await source(debugRoot, "DebugLimits.cs");
  for (const value of [
    "RunCompareDefaultContext = 1",
    "MaxRunCompareContext = 4",
    "MaxRunCompareEvents = 32",
    "MaxRunCompareValueBytes = 2 * 1024",
    "MaxRunComparePathSegments = 8",
    "MaxRunCompareConditionDifferences = 8",
  ]) {
    assert.ok(limits.includes(value), `DebugLimits must declare ${value}`);
  }
  assert.match(limits, /internal static string RunCompareSummary\(\)/);
  const kernel = await source(debugRoot, "DebugRunCompare.cs");
  const tool = await source(runToolsRoot, "../run-compare.cs");
  for (const text of [kernel, tool]) {
    assert.doesNotMatch(text, /MaxRunCompare\w+ = \d/, "compare bounds must only be declared in DebugLimits");
    assert.doesNotMatch(text, /RunCompareDefaultContext = \d/, "the default context must come from DebugLimits");
  }
  assert.match(tool, /DebugLimits\.RunCompareSummary\(\)/, "the descriptor publishes the single compare limit summary");
  assert.match(kernel, /DebugLimits\.MaxRunCompareEvents/, "the event view budget comes from the limit owner");
  assert.match(kernel, /DebugLimits\.MaxRunCompareConditionDifferences/, "the condition difference budget comes from the limit owner");
});
