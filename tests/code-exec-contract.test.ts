import assert from "node:assert/strict";
import test from "node:test";
import codeExec from "../src/tools/code/exec.js";

test("code.exec publishes direct examples with and without a completion probe", () => {
  const plainExample = codeExec.examples?.[0];
  const probeExample = codeExec.examples?.[1];
  assert.ok(plainExample);
  assert.ok(probeExample);
  assert.deepEqual(plainExample.argv?.slice(0, 3), ["tools", "run", "code.exec"]);
  const plainInputIndex = plainExample.argv!.indexOf("--input");
  const plainInput = JSON.parse(plainExample.argv![plainInputIndex + 1]!) as Record<string, unknown>;
  assert.equal(typeof plainInput.code, "string");
  assert.equal("completionProbe" in plainInput, false);
  const probeInputIndex = probeExample.argv!.indexOf("--input");
  const probeInput = JSON.parse(probeExample.argv![probeInputIndex + 1]!) as Record<string, unknown>;
  assert.ok(probeInput.completionProbe && typeof probeInput.completionProbe === "object");
  assert.equal(plainExample.argv!.includes("--dry-run"), false);
  assert.equal(probeExample.argv!.includes("--dry-run"), false);
  assert.doesNotMatch(JSON.stringify(codeExec.examples), /\{\.\.\.\}|--channel|batchmode|--job/);
});
