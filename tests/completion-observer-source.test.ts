import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = (path: string) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("completion proof uses a private read Tool observer registry", async () => {
  const [contract, probe] = await Promise.all([
    source("unitypkg/Editor/ProjectTools/IUnityCliTool.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliCompletionProbe.cs"),
  ]);

  assert.match(contract, /public string ReadTool \{ get; set; \}/);
  assert.doesNotMatch(contract, /IUnityCliProofObserver|public string Context \{ get; set; \}/);
  assert.match(probe, /private static readonly Dictionary<string, CompletionProofObserverDefinition> CompletionProofObservers/);
  assert.match(probe, /"scene\.gameobject"/);
  assert.match(probe, /"scene\.gameobject\.component"/);
  assert.match(probe, /CompletionProofObservers\.TryGetValue\(probe\.ReadTool/);
  assert.doesNotMatch(probe, /DiscoverContexts\(|RegisteredContext|Contexts\.TryGetValue/);
});
