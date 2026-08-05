import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const coordinatorPath = join(process.cwd(), "unitypkg", "Editor", "Infrastructure", "UnityCliLiveRefreshCoordinator.cs");
const skeletonPath = join(process.cwd(), "unitypkg", "Editor", "Infrastructure", "UnityCliReloadOperation.cs");

test("refresh persists and resumes only its active operation", async () => {
  const source = await readFile(coordinatorPath, "utf8");
  const skeleton = await readFile(skeletonPath, "utf8");
  // refresh 收编进统一骨架：scope 声明与业务状态机在协调器内，active 文件与 lease 生命周期由骨架承担。
  assert.match(source, /Scope \{ get \{ return "refresh"; \} \}/);
  assert.match(source, /BeforeReload\(\) \{ Instance\.Update\(x => x\["reloadObserved"\] = true\); \}/);
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
