import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const coordinatorPath = join(process.cwd(), "unitypkg", "Editor", "Infrastructure", "UnityCliLiveRefreshCoordinator.cs");
const skeletonPath = join(process.cwd(), "unitypkg", "Editor", "Infrastructure", "UnityCliReloadOperation.cs");
const editorRefreshToolPath = join(process.cwd(), "src", "tools", "editor", "refresh.ts");
const assetRefreshToolPath = join(process.cwd(), "src", "tools", "asset", "refresh.ts");

test("refresh persists and resumes only its active operation", async () => {
  const source = await readFile(coordinatorPath, "utf8");
  const skeleton = await readFile(skeletonPath, "utf8");
  // refresh 收编进统一骨架：scope 声明与业务状态机在协调器内，active 文件与 lease 生命周期由骨架承担。
  assert.match(source, /Scope \{ get \{ return "refresh"; \} \}/);
  assert.match(source, /BeforeReload\(\)\s*\{\s*Instance\.Update\(\w+ => \w+\["reloadObserved"\] = true\);\s*\}/);
  assert.match(source, /UnityCliReloadOperation/);
  assert.match(skeleton, /UnityCliActiveOperation\.Exists\(Scope\)/);
  assert.match(skeleton, /UnityCliActiveOperation\.Load\(Scope\)/);
  assert.match(skeleton, /UnityCliActiveOperation\.Save\(Scope, root\)/);
  assert.match(skeleton, /UnityCliSessionMutationLease\.TryAcquire/);
  assert.match(skeleton, /UnityCliSessionMutationLease\.Release/);
  assert.doesNotMatch(source, /RequestJournal|AcceptOrRecover|MarkEffectStarted|E_REQUEST_OUTCOME_UNKNOWN|E_REQUEST_JOURNAL/);
});

test("refresh maps unconfirmed outcomes to execution timeout and clears active state", async () => {
  const source = await readFile(coordinatorPath, "utf8");
  const skeleton = await readFile(skeletonPath, "utf8");
  assert.match(source, /E_EXECUTION_TIMEOUT/);
  assert.match(source, /result cannot be confirmed/);
  assert.match(skeleton, /UnityCliActiveOperation\.Clear\(Scope\)/);
});

test("refresh uses bounded Unity-owned artifact generations instead of recursive file proof", async () => {
  const source = await readFile(coordinatorPath, "utf8");
  assert.match(source, /AssetDatabase\.GlobalArtifactDependencyVersion/);
  assert.match(source, /AssetDatabase\.GlobalArtifactProcessedVersion/);
  assert.match(source, /"importObserved"/);
  assert.doesNotMatch(source, /Directory\.GetFiles|SearchOption\.AllDirectories|GetAssetDependencyHash|HashFile|sourceFingerprint|sourceFileCount/);
});

test("refresh observes Unity compilation without dispatching a second compile request", async () => {
  const source = await readFile(coordinatorPath, "utf8");
  const observation = source.indexOf("CurrentCompilationEventSequence()");
  const refresh = source.indexOf("AssetDatabase.Refresh()");
  assert.ok(observation >= 0 && refresh > observation);
  assert.match(source, /"compilationObservationStarted"/);
  assert.match(source, /"compileObservationSequence"/);
  assert.doesNotMatch(source, /RequestRefreshCompilation|RequestScriptCompilation|compile-dispatch/);
});

test("reload refresh status observers rebind without the root listener admission", async () => {
  for (const [path, command] of [
    [editorRefreshToolPath, "internal.editor.refresh.status"],
    [assetRefreshToolPath, "internal.asset.refresh.status"],
  ] as const) {
    const source = await readFile(path, "utf8");
    assert.match(source, new RegExp(`${command.replaceAll(".", "\\.")}[^\\n]+observe, false\\)`));
    assert.match(source, /includeRootAdmission\s*\?\s*\{ dataDir: context\.dataDir, rootAdmission: context\.rootAdmission, bridgeExecute: context\.bridgeExecute \}/);
  }
});
