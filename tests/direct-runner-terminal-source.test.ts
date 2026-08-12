import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("C# direct runner and completion driver share the one-field terminal shape", async () => {
  const [registry, coordinator] = await Promise.all([
    readFile("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs", "utf8")
  ]);

  assert.match(registry, /return new Dictionary<string, object> \{ \{ "result", result \} \}/);
  assert.match(coordinator, /data\.Count != 1[\s\S]*data\.TryGetValue\("result", out resultValue\)/);
  assert.doesNotMatch(coordinator, /data\.TryGetValue\("command"|execution\.TryGetValue\("exitState"/);
});

test("C# no-effect completion is a closed request-specific Tool set", async () => {
  const coordinator = await readFile("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs", "utf8");

  assert.match(coordinator, /RequestRequiresMutation\(request, contract\)/);
  assert.match(coordinator, /if \(!mutation\)[\s\S]*ValidateCoordinatedTerminal\(response, request\.RequestId\)/);
  assert.doesNotMatch(coordinator, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
});

test("Build target and scene no-ops produce approved request-specific terminals", async () => {
  const [platform, scene, dtos] = await Promise.all([
    readFile("unitypkg/Editor/Tools/build/platform/set.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/build/scene/add.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/build/BuildToolDtos.cs", "utf8")
  ]);
  assert.match(dtos, /BuildPlatformResult[^\n]*bool Changed[^\n]*string NoOpReason/);
  assert.match(dtos, /BuildSceneMutationResult[^\n]*bool Changed[^\n]*string NoOpReason/);
  assert.match(platform, /changed = EditorUserBuildSettings\.activeBuildTarget != target/);
  assert.match(scene, /NoOpReason = !ctx\.DryRun && !changed \? "scene-already-enabled" : null/);
});

test("C# Tool runner reports effect-barrier timeouts uniformly", async () => {
  const [registry, runtime, context] = await Promise.all([
    readFile("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/UnityCliProjectToolContext.cs", "utf8")
  ]);
  assert.match(registry, /executionContext\.EffectCommitted[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(runtime, /executionContext\.EffectCommitted[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(context, /EffectCommitted \{ get \{ return Volatile\.Read\(ref commitEffectState\) == 1; \} \}/);
  assert.match(context, /Interlocked\.CompareExchange\(ref commitEffectState, -1, 0\)[\s\S]*CommitEffectAction\(\);[\s\S]*Volatile\.Write\(ref commitEffectState, 1\)[\s\S]*catch[\s\S]*Volatile\.Write\(ref commitEffectState, 0\)/);
  assert.doesNotMatch(`${registry}\n${runtime}\n${context}`, /E_REQUEST_OUTCOME_UNKNOWN|E_REQUEST_JOURNAL/);
});
