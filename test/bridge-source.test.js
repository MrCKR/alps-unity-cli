import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";

test("Unity Editor Bridge waits for heartbeat request completion before disposing request", async () => {
  const sourcePath = path.join(import.meta.dirname, "..", "unity-package", "Editor", "AlpsUnityBridge.cs");
  const source = await fs.promises.readFile(sourcePath, "utf8");

  assert.match(source, /var operation = request\.SendWebRequest\(\);/);
  assert.match(source, /while \(!operation\.isDone\)/);
  assert.match(source, /yield return null;/);
  assert.doesNotMatch(source, /yield return request\.SendWebRequest\(\);/);

  const waitIndex = source.indexOf("while (!operation.isDone)");
  const resultIndex = source.indexOf("if (request.result != UnityWebRequest.Result.Success)");
  const disposeIndex = source.indexOf("private static void WriteBridgeLog");
  assert.ok(waitIndex > -1);
  assert.ok(resultIndex > waitIndex);
  assert.ok(disposeIndex > resultIndex);
});
