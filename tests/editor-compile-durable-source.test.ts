import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = (path: string) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("compile persists only its active operation and retains the mutation lease", async () => {
  const [coordinator, operation] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs"),
  ]);
  assert.match(coordinator, /class Coordinator : UnityCliReloadOperation/);
  assert.match(coordinator, /Scope \{ get \{ return "compile"; \} \}/);
  assert.match(operation, /UnityCliActiveOperation\.Exists\(Scope\)/);
  assert.match(operation, /UnityCliActiveOperation\.Load\(Scope\)/);
  assert.match(operation, /UnityCliActiveOperation\.Save\(Scope, root\)/);
  assert.match(operation, /UnityCliActiveOperation\.Clear\(Scope\)/);
  assert.match(operation, /UnityCliSessionMutationLease\.TryAcquire/);
  assert.match(operation, /UnityCliSessionMutationLease\.Release/);
  assert.match(coordinator, /compile-sequence\.txt/);
  assert.match(coordinator, /"runId", "compile-" \+ digest\.Substring\(0, 32\)/);
  assert.match(coordinator, /OnCompilationStarted|OnAssemblyCompilationFinished|OnCompilationFinished/);
  assert.doesNotMatch(coordinator, /RequestJournal|AcceptOrRecover|MarkEffectStarted|Directory\.GetFiles|E_REQUEST_OUTCOME_UNKNOWN|E_REQUEST_JOURNAL/);
});

test("compile converts indeterminate operation outcomes to execution timeout", async () => {
  const coordinator = await source("unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs");
  assert.match(coordinator, /E_EXECUTION_TIMEOUT/);
  assert.match(coordinator, /result cannot be confirmed/);
  assert.doesNotMatch(coordinator, /E_REQUEST_OUTCOME_UNKNOWN/);
});
