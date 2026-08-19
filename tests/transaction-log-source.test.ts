import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const sourcePath = "../unitypkg/Editor/Infrastructure/UnityCliTransactionLog.cs";

test("transaction log appends embedded JSONL transactions within two bounded segments", async () => {
  const source = await readFile(new URL(sourcePath, import.meta.url), "utf8");

  assert.match(source, /internal static class UnityCliTransactionLog/);
  assert.match(source, /internal static void Append\(string requestId, string command, string requestJson, string responseJson\)/);
  assert.match(source, /internal static void RotateForNewSession\(\)/);
  assert.match(source, /"unity-cli-transactions\.log"/);
  assert.match(source, /"unity-cli-transactions\.old\.log"/);
  assert.match(source, /Directory\.GetParent\(Application\.dataPath\)\.FullName/);
  assert.match(source, /"time"/);
  assert.match(source, /"requestId"/);
  assert.match(source, /"command"/);
  assert.match(source, /"request"/);
  assert.match(source, /"response"/);
  assert.match(source, /DateTime\.UtcNow\.ToString\("O", CultureInfo\.InvariantCulture\)/);
  assert.match(source, /UnityCliSerializer\.Parse\(requestJson\)/);
  assert.match(source, /UnityCliSerializer\.Parse\(responseJson\)/);
  assert.match(source, /MaxLogFileBytes = 16L \* 1024L \* 1024L/);
  assert.match(source, /private static readonly object Sync/);
  assert.match(source, /encoding\.GetByteCount\(line\)/);
  assert.match(source, /if \(lineBytes > MaxLogFileBytes\)/);
  assert.match(source, /EnsureBoundedCapacity\(path, oldPath, lineBytes\)/);
  assert.match(source, /File\.AppendAllText\(path, line, encoding\)/);
  assert.match(source, /SessionState\.GetBool\(SessionRotatedKey, false\)/);
  assert.match(source, /SessionState\.SetBool\(SessionRotatedKey, true\)/);
  assert.match(source, /"UnityCli\.TransactionLog\.Rotated"/);
  assert.match(source, /if \(File\.Exists\(oldPath\) && new FileInfo\(oldPath\)\.Length > MaxLogFileBytes\) File\.Delete\(oldPath\)/);
  assert.match(source, /if \(currentBytes <= MaxLogFileBytes && currentBytes \+ incomingBytes <= MaxLogFileBytes\) return/);
  assert.match(source, /if \(File\.Exists\(oldPath\)\) File\.Delete\(oldPath\)/);
  assert.match(source, /if \(new FileInfo\(path\)\.Length <= MaxLogFileBytes\) File\.Move\(path, oldPath\)/);
  assert.match(source, /else File\.Delete\(path\)/);
});

test("Bridge log keeps two absolute-size-bounded UTF-8 segments", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliLog.cs", import.meta.url), "utf8");

  assert.match(source, /BridgeLogMaxBytes = 10L \* 1024 \* 1024/);
  assert.match(source, /BridgeLogMaxEntryBytes = 64 \* 1024/);
  assert.match(source, /RollBridgeLogIfNeeded\(string path, int incomingBytes\)/);
  assert.match(source, /TrimBridgeLogSegment\(oldPath\)/);
  assert.match(source, /TrimBridgeLogSegment\(path\)/);
  assert.match(source, /new FileInfo\(path\)\.Length \+ incomingBytes <= BridgeLogMaxBytes/);
  assert.match(source, /File\.Copy\(path, oldPath, true\)/);
  assert.match(source, /BoundedBridgeLogLine\(line, lineBudget\)/);
  assert.match(source, /incomingBytes = Encoding\.UTF8\.GetByteCount\(entry\)/);
  assert.match(source, /File\.AppendAllText\(path, entry, new UTF8Encoding\(false\)\)/);
  assert.match(source, /Encoding\.UTF8\.GetByteCount\(text\.Substring\(middle\)\) <= BridgeLogMaxBytes/);
  assert.match(source, /File\.WriteAllText\(path, text\.Substring\(low\), new UTF8Encoding\(false\)\)/);
});

test("transaction logging is wired into bridge completion and startup", async () => {
  const [queue, bridge] = await Promise.all([
    readFile(new URL("../unitypkg/Editor/Bridge/BridgeWorkQueue.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/UnityCliBridge.cs", import.meta.url), "utf8")
  ]);

  assert.match(queue, /CompleteWorkItem\(BridgeWorkItem item\)[\s\S]*UnityCliTransactionLog\.Append\(item\.Request\.RequestId, item\.Request\.Command, Encoding\.UTF8\.GetString\(item\.RawRequestBytes\), item\.Response/s);
  assert.match(bridge, /WriteRuntimeLoadDiagnostics\("static_constructor"\);\s*UnityCliTransactionLog\.RotateForNewSession\(\);/s);
});

test("unconfirmed-outcome errors use the consolidated codes", async () => {
  const root = new URL("../unitypkg/Editor/Tools/", import.meta.url);
  const [frame, crossfade] = await Promise.all([
    readFile(new URL("scene/view-frame.cs", root), "utf8"),
    readFile(new URL("animation/animator/crossfade.cs", root), "utf8")
  ]);

  for (const source of [frame, crossfade]) {
    assert.match(source, /E_EXECUTION_TIMEOUT/);
    assert.match(source, /result cannot be confirmed/);
  }
});

test("transaction logging warns on failure and has no request journal dependency", async () => {
  const source = await readFile(new URL(sourcePath, import.meta.url), "utf8");

  assert.match(source, /catch \(Exception exception\)[\s\S]*Debug\.LogWarning/s);
  assert.doesNotMatch(source, /throw\s+(?:new\s+)?/);
  assert.doesNotMatch(source, /RequestJournal/);
});
