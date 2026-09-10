import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

test("targeted observed-state tools use stable fresh readback before returning", async () => {
  const paths = [
    "../unitypkg/Editor/Tools/animation-clip/animator/play.cs",
    "../unitypkg/Editor/Tools/animation-clip/animator/stop.cs",
    "../unitypkg/Editor/Tools/editor/pause.cs",
    "../unitypkg/Editor/Tools/rendering/lighting/bake/cancel.cs",
    "../unitypkg/Editor/Tools/rendering/lighting/bake/start.cs",
  ];
  for (const path of paths) assert.match(await source(path), /ctx\.Wait\.UntilStable\(/, path);
});

test("plain tools abort exact direct owners across reloads and active operation files keep fixed names", async () => {
  const [coordinator, activeOperation, context] = await Promise.all([
    source("../unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs"),
    source("../unitypkg/Editor/Infrastructure/UnityCliActiveOperation.cs"),
    source("../unitypkg/Editor/ProjectTools/UnityCliProjectToolContext.cs"),
  ]);

  assert.match(coordinator, /普通direct mutation没有durable recovery root，只能使用本次新取得的exact lease/);
  assert.match(coordinator, /TryFinalizeRecoveredPlayModeRoot[\s\S]*DirectMutationCleanupPendingSessionKey[\s\S]*ReleaseDirectMutationLease\(requestId, requestDigest\)/);
  assert.match(activeOperation, /"active-" \+ scope \+ "\.json"/);
  assert.doesNotMatch(`${coordinator}\n${context}`, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN|E_REQUEST_JOURNAL|ProofOnly/);
});
