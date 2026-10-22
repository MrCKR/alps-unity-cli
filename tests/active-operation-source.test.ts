import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const readSource = () => readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliActiveOperation.cs", import.meta.url), "utf8");

test("active operation exposes one durable state file per scope", async () => {
  const source = await readSource();

  for (const api of ["Load", "Save", "Clear", "Exists"])
    assert.match(source, new RegExp(`internal static (?:Dictionary<string, object>|void|bool) ${api}\\(string scope`));
  assert.match(source, /Path\.Combine\(projectRoot, "Library", "UnityCli", "active-" \+ scope \+ "\.json"\)/);
  assert.match(source, /MoveFileExW\(temporary, path, MoveFileReplaceExisting \| MoveFileWriteThrough\)/);
  assert.match(source, /FileOptions\.WriteThrough/);
  assert.match(source, /stream\.Flush\(true\)/);
  assert.doesNotMatch(source, /Directory\.GetFiles/);
  assert.doesNotMatch(source, /Ttl/);
  assert.match(source, /StoreTerminalFirst\(string scope, string requestId, string requestDigest/);
  assert.match(source, /Entries\.Count > 100/);
  assert.match(source, /MaxTerminalEntryBytes = 2 \* 1024 \* 1024/);
  assert.match(source, /Encoding\.UTF8\.GetByteCount\(serialized\) > MaxTerminalCacheBytes/);
  assert.match(source, /RetireTerminalEntry\(cache, retired\)[\s\S]*RemoveAt\(0\)/);
  assert.match(source, /EnsureTerminalAdmission[\s\S]*RetiredIdentities\.Count \+ TerminalAdmissionIdentityReserve < MaxRetiredTerminalIdentities\) return;/);
  assert.match(source, /RetiredRetentionUntilMs[\s\S]*TryCaptureAdmissionDeadlineMs[\s\S]*RetiredRetentionGraceMs/);
  assert.match(source, /if \(\(long\)retainUntilValue <= now\) continue;/);
  assert.match(source, /"E_UNITY_BUSY"/);
  assert.doesNotMatch(source, /restart the Editor session before submitting another mutation/);
  assert.doesNotMatch(source, /retiredBloom|MightContainRetiredTerminal/);
  assert.match(source, /E_REQUEST_ID_REUSED/);
});

test("ordinary requests retain no compatibility journal", async () => {
  const coordinator = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs", import.meta.url), "utf8");

  assert.match(coordinator, /普通 Tool 不创建请求级文件/);
  assert.doesNotMatch(coordinator, /RequestJournal|E_REQUEST_JOURNAL|E_REQUEST_OUTCOME_UNKNOWN/);
});
