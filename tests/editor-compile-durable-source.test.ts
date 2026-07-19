import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = (path: string) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("durable compile broker is the sole CompilationPipeline subscriber", async () => {
  const [broker, coordinator, bridge] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliCompilationEventBroker.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs"),
    source("unitypkg/Editor/UnityCliBridge.cs")
  ]);
  assert.equal((broker.match(/CompilationPipeline\.[A-Za-z]+ \+=/g) ?? []).length, 3);
  assert.equal((broker.match(/CompilationPipeline\.[A-Za-z]+ \-=/g) ?? []).length, 3);
  assert.match(broker, /NextCompilationEventSequence/);
  assert.match(coordinator, /OnAssemblyCompilationFinished/);
  assert.match(coordinator, /internal static void Register\(BridgeCommandRegistry registry\)/);
  assert.match(bridge, /UnityCliCompilationEventBroker\.Initialize\(\)/);
  assert.match(bridge, /UnityCliCompilationEventBroker\.BeforeReload\(\)/);
});

test("compile journal remains v1 and has atomic durable acceptance inputs", async () => {
  const [journal, coordinator, generated] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliRequestJournal.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs"),
    source("unitypkg/Editor/Generated/UnityCliToolCompletionRegistry.g.cs")
  ]);
  assert.match(journal, /RequestJournalVersion = 1/);
  assert.match(journal, /"compileOperation"/);
  assert.match(journal, /ValidateCompileOperation/);
  assert.match(journal, /compileOperation fields are incomplete or unknown/);
  assert.match(journal, /compileOperation runId does not match the request digest/);
  assert.match(journal, /compileOperation start attribution is invalid/);
  assert.match(coordinator, /NextSequenceLocked/);
  assert.match(coordinator, /RequestDigest/);
  assert.match(coordinator, /AcceptOrRecover\([\s\S]*UnityCliToolCompletionRegistry\.EditorCompile,[\s\S]*InitializeAcceptedOperation\(record, args\)/);
  assert.match(coordinator, /TerminalStartResponse\(request\.RequestId, decision\.TerminalEnvelope\)/);
  assert.match(coordinator, /record\.CompileOperation = NewOperation\(record\.RequestDigest, args, NextSequenceLocked\(\)\)/);
  assert.doesNotMatch(coordinator, /EnsureAcceptedOperation/);
  assert.doesNotMatch(coordinator, /new UnityCliToolCompletionContract/);
  assert.match(generated, /EditorCompile = new UnityCliToolCompletionContract\("editor\.compile", "ts-local", "direct-terminal", "resumable", "durable-operation-terminal", "journal-continuation-and-terminal-proof"/);
  assert.match(coordinator, /private static void CommitBarrierAndDispatch[\s\S]*MarkEffectStarted\(requestId, "compile-operation-v1", continuation\)/);
  assert.match(coordinator, /"triggerEventSequence"/);
  assert.match(coordinator, /StableFrames = 3/);
  assert.match(coordinator, /TickCadence = TimeSpan\.FromMilliseconds\(100\)/);
  assert.match(coordinator, /DateTimeOffset\.TryParse\(Text\(op, "stableSince"\), CultureInfo\.InvariantCulture, DateTimeStyles\.AssumeUniversal \| DateTimeStyles\.AdjustToUniversal/);
  assert.doesNotMatch(coordinator, /DateTime\.TryParse\(Text\(op, "stableSince"\)/);
});

test("compile terminal data is public-redacted and only complete attributed cycles are proven", async () => {
  const coordinator = await source("unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs");
  const data = coordinator.slice(coordinator.indexOf("private static object CompileData"), coordinator.indexOf("private static object CompileFailed"));
  assert.match(coordinator, /E_UNITY_COMPILE_FAILED/);
  assert.match(coordinator, /E_COMPILE_CYCLE_UNATTRIBUTED/);
  assert.match(coordinator, /E_UNITY_COMPILE_REQUIRES_EDIT_MODE/);
  assert.match(coordinator, /CallbackUnattributed/);
  assert.match(coordinator, /MarkEffectProven[\s\S]*PersistTerminal/);
  assert.match(coordinator, /Unattributed\(RequestJournalRecord record, string message\) \{ Unknown\(record, message, "E_COMPILE_CYCLE_UNATTRIBUTED"\)/);
  assert.match(coordinator, /CompileOutcomeUnknown[\s\S]*"E_REQUEST_OUTCOME_UNKNOWN"/);
  assert.match(coordinator, /CommonCompileSummary/);
  assert.match(coordinator, /"assemblies"/);
  assert.match(coordinator, /assemblyCount", assemblies\.Count/);
  assert.match(coordinator, /"status", transition \? "transitioning" : "ready"/);
  assert.match(coordinator, /"playMode", new Dictionary<string, object>/);
  assert.match(coordinator, /PreEffectPhase/);
  assert.match(coordinator, /ObservePreexistingCallback/);
  assert.match(coordinator, /Compilation started callback was missing before the deadline/);
  assert.match(coordinator, /Compilation finished callback was missing before the deadline/);
  assert.match(coordinator, /CommitBarrierAndDispatch\(current\.RequestId, snapshot, false\)/);
  assert.match(coordinator, /SetPhase\(requestId, "dispatching", snapshot\);[\s\S]*Dispatch\(record\)/);
  assert.doesNotMatch(coordinator, /if \(phase == "dispatching"\) \{ Dispatch\(current\)/);
  assert.match(coordinator, /if \(phase == "dispatching"\)[\s\S]*activeDispatchRequestId[\s\S]*Unknown\(current/);
  assert.match(coordinator, /activeDispatchRequestId = requestId;[\s\S]*finally/);
  assert.match(coordinator, /"terminal", false[\s\S]*"accepted", true/);
  assert.match(coordinator, /TerminalStartResponse[\s\S]*"terminal", true[\s\S]*"rootEnvelope"/);
  assert.match(coordinator, /compile_dispatch_failed requestId=/);
  assert.doesNotMatch(coordinator, /Unknown\(record, "Compile dispatch was ambiguous: " \+ exception\.Message/);
  assert.match(coordinator, /BeforeOwnedDispatch[\s\S]*phase == "trigger_committed"/);
  assert.match(coordinator, /Text\(live, "phase"\) == "dispatching" && Long\(live, "startedCycle"\) == 0/);
  assert.match(data, /"diagnostics"/);
  assert.match(data, /PublicEditorState/);
  assert.doesNotMatch(data, /runId|sequence|lease|listener|port|SampleEditorStateSnapshot/);
});

test("compile recovery and closed durable operation preserve attribution boundaries", async () => {
  const [journal, broker, coordinator] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliRequestJournal.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliCompilationEventBroker.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs")
  ]);
  assert.match(journal, /ResumeFinalize = true, TerminalEnvelope = record\.ProvenEnvelope/);
  assert.match(journal, /originating Editor session ended before stable finalization/);
  assert.match(journal, /if \(!string\.Equals\(record\.ToolName, "editor\.compile", StringComparison\.Ordinal\)[\s\S]*record\.State = ProvenEnvelopeSucceeded\(record\.ProvenEnvelope\) \? "completed" : "failed"/);
  assert.match(journal, /ResumeFinalize = true/);
  assert.match(journal, /"assemblies"/);
  assert.match(journal, /"info"/);
  assert.match(broker, /sequence = UnityCliBridge\.CurrentCompilationEventSequence\(\)/);
  assert.match(broker, /digits == 4/);
  assert.match(coordinator, /if \(!string\.IsNullOrEmpty\(leasedRequestId\)\)[\s\S]*var leasedPath = RequestJournalPath\(leasedRequestId\)/);
  assert.match(coordinator, /TerminalizeOldSessionLocked/);
  assert.match(coordinator, /E_UNITY_COMPILE_REQUIRES_EDIT_MODE/);
  assert.match(coordinator, /PlayModeStable\(\)[\s\S]*!IsPlayModeChanging\(\)/);
  assert.match(coordinator, /ReadyAfterCompile\(\)[\s\S]*PlayModeStable\(\)/);
});

test("ordinary mutations are rejected before journal acceptance while compile owns the lane", async () => {
  const operations = await source("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs");
  const gate = operations.slice(operations.indexOf("UnityCliCompileCoordinator.IsActive"), operations.indexOf("AcceptOrRecover", operations.indexOf("UnityCliCompileCoordinator.IsActive")));
  assert.match(gate, /E_UNITY_COMPILING/);
  assert.match(gate, /transient: true/);
  assert.doesNotMatch(gate, /PersistTerminal/);
});
