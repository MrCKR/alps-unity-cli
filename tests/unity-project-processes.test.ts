import assert from "node:assert/strict";
import test from "node:test";
import {
  closeUnityProjectProcesses,
  parseUnityProjectProcessDiscoveryOutput,
  WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT,
} from "../src/unity-project-processes.js";

test("Unity process discovery keeps interactive Editors and excludes AssetImport workers", () => {
  const stdout = JSON.stringify([
    { processId: 10, processStartedAt: "2026-08-31T00:00:00.000Z", executablePath: "C:\\Unity\\Unity.exe", argv: ["Unity.exe", "-projectPath", "D:\\Game"] },
    { processId: 11, processStartedAt: "2026-08-31T00:00:01.000Z", executablePath: "C:\\Unity\\Unity.exe", argv: ["Unity.exe", "-batchMode", "-name", "AssetImportWorker0", "-projectPath", "D:\\Game"] },
  ]);
  assert.deepEqual(parseUnityProjectProcessDiscoveryOutput(stdout).map((item) => item.processId), [10]);
});

test("normal Editor close script preserves process identity and never force-terminates", async () => {
  assert.match(WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT, /CreationDate/);
  assert.match(WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT, /processStartedAt/);
  assert.match(WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT, /ToUnixTimeMilliseconds/);
  assert.doesNotMatch(WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT, /\.Ticks\s+-ne/);
  assert.match(WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT, /CloseMainWindow/);
  assert.doesNotMatch(WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT, /Stop-Process|taskkill|TerminateProcess|\.Kill\s*\(/i);
  if (process.platform === "win32") assert.deepEqual(await closeUnityProjectProcesses([], 100), { requested: 0, remaining: 0 });
});
