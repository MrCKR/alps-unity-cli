import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../", import.meta.url);
async function source(path: string): Promise<string> { return readFile(new URL(path, root), "utf8"); }

test("C# mutation execution uses the shared instance lease owner protocol", async () => {
  const [lease, bridge, operation, reloadOperation, workQueue] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliSessionMutationLease.cs"),
    source("unitypkg/Editor/UnityCliBridge.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs"),
    source("unitypkg/Editor/Bridge/BridgeWorkQueue.cs"),
  ]);
  for (const field of ["pid", "token", "processStartIdentity", "requestId"]) assert.match(lease, new RegExp(`\\b${field}\\b`, "i"));
  assert.match(lease, /TryAcquire\(string requestId, string requestDigest, out bool newlyAcquired\)/);
  assert.match(lease, /UnityCliActiveOperation\.Exists\(scope\)[\s\S]*UnityCliActiveOperation\.Load\(scope\)/);
  assert.match(bridge, /UnityCliSessionMutationLease\.Initialize\(\);\s*UnityCliCompilationEventBroker\.Initialize\(\);/s);
  assert.match(workQueue, /UnityCliSessionMutationLease\.TryFinalizeOwnedPlayModeRoot\(\);/);
  assert.match(operation, /UnityCliSessionMutationLease\.TryAcquire\([\s\S]*finally[\s\S]*UnityCliSessionMutationLease\.Release\(request\.RequestId\)/);
  assert.match(reloadOperation, /UnityCliSessionMutationLease\.TryAcquire\([\s\S]*UnityCliSessionMutationLease\.Release\(request\.RequestId\)/s);
  assert.match(reloadOperation, /Terminal\([\s\S]*UnityCliSessionMutationLease\.Release\(Text\(root, "requestId"\)\)/s);
  assert.doesNotMatch(operation, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
});

test("all durable mutation coordinators keep the shared lease until canonical terminal", async () => {
  const paths = [
    "unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs",
    "unitypkg/Editor/Infrastructure/UnityCliLiveRefreshCoordinator.cs",
    "unitypkg/Editor/Infrastructure/UnityCliPackageResolveCoordinator.cs",
    "unitypkg/Editor/Infrastructure/UnityCliTestRunCoordinator.cs",
    "unitypkg/Editor/Infrastructure/UnityCliLiveBuildCoordinator.cs",
  ];
  const [reloadOperation, ...coordinators] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs"),
    ...paths.map(source),
  ]);
  const combined = coordinators.join("\n");
  assert.match(reloadOperation, /UnityCliSessionMutationLease\.TryAcquire\([\s\S]*Terminal\([\s\S]*UnityCliSessionMutationLease\.Release/s);
  for (const path of paths.slice(0, 4)) assert.match(await source(path), /UnityCliReloadOperation/, path);
  assert.match(await source(paths[4]), /UnityCliSessionMutationLease\.(?:TryAcquire|Release)/);
  assert.match(combined, /UnityCliSessionMutationLease\.IsOwnedBy\(/);
  assert.match(reloadOperation, /UnityCliActiveOperation\.(?:Load|Save|Clear)\(Scope\)/);
  assert.doesNotMatch(combined, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN|E_REQUEST_JOURNAL/);
});

test("Package coordinator uses active-operation state and terminal cleanup", async () => {
  const coordinator = await source("unitypkg/Editor/Infrastructure/UnityCliPackageResolveCoordinator.cs");
  assert.match(coordinator, /private sealed class Coordinator : UnityCliReloadOperation/);
  assert.match(coordinator, /internal static async Task<string> Execute\([\s\S]*operation\.Execute/);
  assert.match(coordinator, /internal static void Tick\(\) \{ operation\.Tick\(\); \}/);
  assert.match(coordinator, /AdditionalCommands[\s\S]*ClaimFilesCommand[\s\S]*CommitFilesCommand[\s\S]*ProveFilesCommand[\s\S]*FailFilesCommand/);
  assert.match(coordinator, /ExecuteAdditionalCommand\([\s\S]*ClaimFiles\(request\)[\s\S]*CommitFiles\(request\)[\s\S]*ProveFiles\(request\)[\s\S]*FailFiles\(request\)/);
  assert.match(coordinator, /AcceptPackageActiveOperation\(request, raw, contract/);
  assert.match(coordinator, /RequireLease\(root\.RequestId\)/);
  assert.match(coordinator, /E_EXECUTION_TIMEOUT/);
  assert.doesNotMatch(coordinator, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
});

test("TestRunner attribution persists dispatch, returned identity, callback identity, and pending finish", async () => {
  const testCoordinator = await source("unitypkg/Editor/Infrastructure/UnityCliTestRunCoordinator.cs");
  assert.match(testCoordinator, /private sealed class Coordinator : UnityCliReloadOperation/);
  assert.match(testCoordinator, /internal static Task<string> Execute\([\s\S]*operation\.Execute/);
  assert.match(testCoordinator, /internal static void Tick\(\) \{ operation\.Tick\(\); \}/);
  for (const field of ["execute_dispatching", "run_identity_pending", "returnedRunId", "observedRunId", "pendingFinished", "CompletePending"])
    assert.match(testCoordinator, new RegExp(field));
  const deadlineRecovery = testCoordinator.slice(testCoordinator.indexOf("private static void RecoverAfterDeadline"), testCoordinator.indexOf("private static void RecoverRunIdentity"));
  assert.match(deadlineRecovery, /ids\.Length == 1 && Same\(ids\[0\], runId\)\) return/);
  assert.match(deadlineRecovery, /Fail\(root, "E_EXECUTION_TIMEOUT"/);
  assert.doesNotMatch(testCoordinator, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
});

test("refresh delegates compile dispatch and callback attribution to compile coordinator", async () => {
  const [refresh, compile] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliLiveRefreshCoordinator.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs"),
  ]);
  assert.match(refresh, /UnityCliCompileCoordinator\.RequestRefreshCompilation/);
  assert.doesNotMatch(refresh, /CompilationPipeline\.RequestScriptCompilation|RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
  // active 文件读写收编进统一骨架；refresh 只声明 scope 并继承骨架。
  assert.match(refresh, /Scope \{ get \{ return "refresh"; \} \}/);
  assert.match(refresh, /UnityCliReloadOperation/);
  assert.match(refresh, /E_EXECUTION_TIMEOUT/);
  assert.match(compile, /RequestRefreshCompilation/);
  assert.match(compile, /UpdateRefresh\(null, x =>/);
  assert.match(compile, /compileDiagnostics/);
});
