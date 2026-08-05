import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const readSource = (path: string) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("ordinary coordinator execution does not use request persistence", async () => {
  const source = await readSource("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs");

  assert.match(source, /tryAcceptJournal == null \|\| !tryAcceptJournal\(\)/);
  assert.match(source, /普通 Tool 不创建请求级文件/);
  assert.match(source, /E_EXECUTION_TIMEOUT/);
  assert.match(source, /Check the affected business state before submitting another request/);
  assert.doesNotMatch(source, /RequestJournal/);
  assert.doesNotMatch(source, /E_REQUEST_OUTCOME_UNKNOWN/);
  assert.doesNotMatch(source, /MarkEffectStarted|AcceptOrRecover|RequestJournalPath|ReadRequestJournalRecord/);
});

test("completion probe maps unconfirmed effects to execution timeout", async () => {
  const source = await readSource("unitypkg/Editor/Infrastructure/UnityCliCompletionProbe.cs");

  assert.match(source, /"E_EXECUTION_TIMEOUT"/);
  assert.match(source, /result cannot be confirmed/);
  assert.match(source, /Check the declared Context and inspect the target resource/);
  assert.doesNotMatch(source, /E_REQUEST_OUTCOME_UNKNOWN/);
});
