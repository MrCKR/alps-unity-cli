import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = "unitypkg/Editor/";

test("verified object patch engine has one direct replace path and no audit/log tree", async () => {
  const source = await readFile(`${root}Infrastructure/UnityCliObjectPatch.cs`, "utf8");
  assert.match(source, /Only non-empty pathPatches on directly writable SerializedProperties/);
  assert.match(source, /ApplyVerifiedSerializedPatches/);
  assert.match(source, /VerifyFreshSerializedProperties/);
  assert.doesNotMatch(source, /ApplyPathPatches|ApplyMergePatch|ResultData|AuditEntries|List<string> Logs/);
});

test("object and component modify results expose only business readback", async () => {
  for (const path of ["Tools/serialized-object/patch.cs", "Tools/component/modify.cs"]) {
    const source = await readFile(`${root}${path}`, "utf8");
    const resultStart = source.indexOf("public sealed class Result");
    const resultEnd = source.indexOf("public ToolDescriptor Describe", resultStart);
    const result = source.slice(resultStart, resultEnd);
    assert.match(result, /ChangedPaths/);
    assert.match(result, /UpdatedData/);
    assert.doesNotMatch(result, /Command|Logs|Audit/);
    assert.doesNotMatch(source, /Command\s*=\s*"(?:object|component)\.modify"/);
    assert.match(source, /VerifyFreshSerializedProperties/);
  }
});
