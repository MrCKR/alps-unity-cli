import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const readSource = (path: string) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("Request journal owns exact-byte digest, private storage, and durable barriers", async () => {
  const source = await readSource("unitypkg/Editor/Infrastructure/UnityCliRequestJournal.cs");
  assert.match(source, /Path\.Combine\(DataDir\(\), "requests", InstanceId\(\)\)/);
  assert.match(source, /"unity-cli-request-v1"/);
  assert.match(source, /Buffer\.BlockCopy\(rawBytes/);
  assert.match(source, /Local\\\\UnityCli\.RequestJournal\./);
  assert.match(source, /"received"/);
  assert.match(source, /"accepted_before_effect"/);
  assert.match(source, /"effect_started"/);
  assert.match(source, /"effect_proven"/);
  assert.match(source, /ResumeFinalize/);
  assert.match(source, /continuationSha256/);
  assert.match(source, /"result_persisted"/);
  assert.match(source, /MoveFileExW\(temporary, path, MoveFileReplaceExisting \| MoveFileWriteThrough\)/);
  assert.match(source, /FileOptions\.WriteThrough/);
  assert.match(source, /stream\.Flush\(true\)/);
  assert.match(source, /E_REQUEST_ID_REUSED/);
  assert.match(source, /E_REQUEST_OUTCOME_UNKNOWN/);
  assert.match(source, /E_REQUEST_JOURNAL_CAPACITY/);
  assert.match(source, /E_REQUEST_JOURNAL_CORRUPT/);
  assert.match(source, /RequestJournalActiveLimit = 256/);
  assert.match(source, /RequestJournalTotalLimit = 4096/);
  assert.match(source, /RequestJournalByteLimit = 64L \* 1024L \* 1024L/);
});

test("journal projection excludes immutable request body and sensitive input", async () => {
  const source = await readSource("unitypkg/Editor/Infrastructure/UnityCliRequestJournal.cs");
  const projection = source.slice(source.indexOf("private static Dictionary<string, object> RequestJournalData"), source.indexOf("private static RequestJournalRecord ReadRequestJournalRecord"));
  assert.doesNotMatch(projection, /rawBytes|requestBody|sourceText|securityProof|password|token/i);
  assert.match(projection, /requestDigest/);
  assert.match(projection, /completionDriverId/);
  assert.match(projection, /provenEnvelope/);
  assert.match(projection, /terminalEnvelope/);
});

test("HTTP and queue route one exact body through one completion coordinator", async () => {
  const [http, queue, json, coordinator, context, registry] = await Promise.all([
    readSource("unitypkg/Editor/Bridge/BridgeHttpServer.cs"),
    readSource("unitypkg/Editor/Bridge/BridgeWorkQueue.cs"),
    readSource("unitypkg/Editor/Infrastructure/UnityCliJson.cs"),
    readSource("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs"),
    readSource("unitypkg/Editor/ProjectTools/UnityCliProjectToolContext.cs"),
    readSource("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs")
  ]);
  assert.match(json, /byte\[\] ReadBodyBytes/);
  assert.match(json, /new UTF8Encoding\(false, true\)/);
  assert.match(json, /UTF-8 BOM/);
  assert.match(http, /x-unity-cli-expected-listener-id/);
  assert.match(http, /x-unity-cli-expected-listener-generation/);
  assert.ok(http.indexOf("x-unity-cli-expected-listener-id") < http.indexOf("EnqueueOrAttach(request, rawBody"));
  assert.match(http, /EnqueueOrAttach\(request, rawBody/);
  assert.match(queue, /ActiveRequestWork/);
  assert.match(queue, /existing\.RequestDigest/);
  assert.match(queue, /ExecuteCoordinatedRequest\(item\.Request, item\.RawRequestBytes/);
  assert.match(coordinator, /MarkAcceptedBeforeEffect/);
  assert.match(coordinator, /MarkEffectStarted/);
  assert.match(coordinator, /MarkEffectProven/);
  assert.match(coordinator, /FinalizeProvenResult/);
  assert.match(coordinator, /UntilEditorReady/);
  assert.match(coordinator, /BuildEffectContinuation/);
  assert.match(coordinator, /asset-content-sha256-v1/);
  assert.match(coordinator, /RecordToolCompletionEvidence/);
  assert.match(coordinator, /TakeCompletionEvidence/);
  assert.match(coordinator, /RequestRequiresMutationJournal/);
  assert.match(coordinator, /PersistTerminal/);
  assert.match(coordinator, /E_TOOL_COMPLETION_CONTRACT_REQUIRED/);
  const prepare = coordinator.slice(coordinator.indexOf("private static EffectContinuation PrepareToolEffect"), coordinator.indexOf("private static void CommitToolEffect"));
  const commit = coordinator.slice(coordinator.indexOf("private static void CommitToolEffect"), coordinator.indexOf("private static bool SupportsProofRecovery"));
  assert.doesNotMatch(prepare, /MarkEffectStarted/);
  assert.match(commit, /MarkEffectStarted\(request\.RequestId/);
  assert.match(context, /Interlocked\.Exchange\(ref commitEffectCalled, 1\) != 0/);
  assert.match(context, /E_TOOL_EFFECT_BARRIER_DUPLICATE/);
  assert.match(registry, /CommitEffectAction = \(\) => CommitToolEffect\(request, definition\.CompletionContract, effectPreparation\)/);
  assert.match(registry, /ProofOnly = IsProofRecoveryRequest\(request\.RequestId\)/);
  assert.doesNotMatch(coordinator, /MarkEffectProven\([^;]+Encoding\.UTF8\.GetBytes\(response\)/s);
  assert.doesNotMatch(coordinator, /listenerId|bridgeEndpoint|actualPort/);
});

test("accepted mutation lifetime is independent from HTTP client disconnect", async () => {
  const [http, queue] = await Promise.all([
    readSource("unitypkg/Editor/Bridge/BridgeHttpServer.cs"),
    readSource("unitypkg/Editor/Bridge/BridgeWorkQueue.cs")
  ]);
  assert.doesNotMatch(http, /context\.Request\.InputStream[^\n]*CancellationToken/);
  assert.doesNotMatch(queue, /client.*disconnect|disconnect.*Cancel/i);
  assert.match(http, /HTTP client 断开不得取消已接收的业务请求/);
  assert.match(http, /TryPersistTimeoutByEffectBarrier\(request\.RequestId, beforeEffectTimeout, out timeoutTerminal\)/);
  assert.match(http, /item\.CancelBeforeJournalAcceptance\(\)/);
  assert.match(queue, /item\.TryAcceptJournal/);
});

test("queue and execution timeout interleavings use one canonical terminal decision", async () => {
  const [http, queue, journal, coordinator] = await Promise.all([
    readSource("unitypkg/Editor/Bridge/BridgeHttpServer.cs"),
    readSource("unitypkg/Editor/Bridge/BridgeWorkQueue.cs"),
    readSource("unitypkg/Editor/Infrastructure/UnityCliRequestJournal.cs"),
    readSource("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs")
  ]);

  // Queue wait expiry is pre-effect only when cancellation wins before TryStart.
  const queueTimeout = http.slice(http.indexOf("if (queueSignal == WaitHandle.WaitTimeout)"), http.indexOf("if (queueSignal == 1)"));
  assert.match(queueTimeout, /if \(item\.CancelBeforeStart\(\)\)/);
  assert.ok(queueTimeout.indexOf("CancelBeforeStart()") < queueTimeout.indexOf("Unity Editor did not start the bridge request"));
  assert.match(queueTimeout, /ResolveStartedTimeoutResponse\(item, request/);
  assert.match(queue, /if \(started\)\s*\{\s*return false;/s);
  assert.match(queue, /public bool TryAcceptJournal\(\)/);
  assert.match(queue, /public bool CancelBeforeJournalAcceptance\(\)/);

  // A terminal response or durable journal result wins over the timeout classifier.
  const resolver = http.slice(http.indexOf("private static string ResolveStartedTimeoutResponse"));
  assert.match(resolver, /var response = item\.Response;\s*if \(response != null\) return response;/s);
  assert.match(resolver, /TryGetRequestJournalTerminal\(request\.RequestId, out terminal\)/);
  assert.ok(resolver.indexOf("TryGetRequestJournalTerminal") < resolver.indexOf("TryPersistTimeoutByEffectBarrier"));
  assert.match(resolver, /TryPersistTimeoutByEffectBarrier\(request\.RequestId, beforeEffectTimeout, out timeoutTerminal\)/);
  assert.match(resolver, /item\.Completed\.Wait\(0\)/);

  // Competing terminal writes always return the already durable envelope to HTTP.
  const persist = journal.slice(journal.indexOf("private static object PersistTerminal"), journal.indexOf("private static RequestJournalDecision RecoverInterrupted"));
  assert.match(persist, /if \(IsTerminal\(record\)\)\s*\{\s*canonicalEnvelope = record\.TerminalEnvelope;/s);
  assert.match(persist, /return canonicalEnvelope;/);
  assert.match(persist, /record\.Phase == "effect_proven" && \(terminalState == "outcome_unknown" \|\| terminalState == "cancelled"\)/);
  assert.match(persist, /ProvenFinalizationFailureEnvelope/);
  assert.match(journal, /private static bool TryGetRequestJournalTerminal/);
  assert.match(journal, /private static object PersistTimeoutByEffectBarrier/);
  assert.match(journal, /private static bool TryPersistTimeoutByEffectBarrier/);
  assert.match(journal, /if \(beforeAccess != null\) beforeAccess\(\);[\s\S]*WriteRequestJournalRecord\(path, createdRecord\)/);
  assert.match(journal, /record\.Phase == "effect_proven"[\s\S]*record\.TerminalEnvelope = record\.ProvenEnvelope/);
  assert.match(journal, /record\.Phase == "effect_started"[\s\S]*record\.State = "outcome_unknown"/);
  assert.match(journal, /if \(IsTerminal\(record\)\) throw JournalCorrupt\("Effect cannot start after a terminal result was persisted\."\);/);
  assert.match(coordinator, /return UnityCliSerializer\.Serialize\(PersistTerminal\(requestId, envelope, "outcome_unknown"\)\)/);
  assert.match(coordinator, /return UnityCliSerializer\.Serialize\(PersistTerminal\(request\.RequestId, provenEnvelope, "completed"\)\)/);
});

test("completion driver rejects intermediate state without field-name proof heuristics", async () => {
  const source = await readSource("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs");
  assert.match(source, /string\.Equals\(key, "state"[\s\S]*string\.Equals\(key, "phase"/);
  assert.match(source, /Non-atomic Tool result is not a non-empty typed business terminal/);
  assert.doesNotMatch(source, /ContainsReadbackSignal|ContainsExternalProofSignal|ContainsNamedValue/);
});

test("input-dependent getter modes do not enter the mutation journal", async () => {
  const source = await readSource("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs");
  assert.match(source, /name == "profiler\.deep"[^\n]+HasAnyInput\(input, "enabled"\)/);
  assert.match(source, /name == "physics\.rigidbody"[^\n]+"mass"/);
  assert.match(source, /name == "physics\.simulation"[^\n]+InputBoolean\(input, "simulate"\)/);
  assert.match(source, /name == "cinemachine\.lens"[^\n]+"fieldOfView"/);
  assert.match(source, /name == "ui\.modify-visual-element"[^\n]+HasUiVisualElementMutation\(input\)/);
  assert.match(source, /HasUiVisualElementMutation[\s\S]+TryGetValue\("text"/);
});
