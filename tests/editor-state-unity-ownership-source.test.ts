import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const root = process.cwd();
const editor = join(root, "unitypkg", "Editor");
const privateCommand = "internal.editor.state.snapshot";

test("editor.state has zero C# Tool or Context owners", async () => {
  await assert.rejects(access(join(editor, "Tools", "editor", "state.cs")));
  await assert.rejects(access(join(editor, "Contexts", "editor", "state.cs")));

  const [tools, contexts, completion] = await Promise.all([
    readFile(join(editor, "UnityCliBridge.cs"), "utf8"),
    readFile(join(editor, "Infrastructure", "UnityCliEditorStateSnapshot.cs"), "utf8"),
    readFile(join(editor, "Generated", "UnityCliToolCompletionRegistry.g.cs"), "utf8"),
  ]);
  assert.doesNotMatch(tools, /"editor\.state"/);
  assert.doesNotMatch(contexts, /IUnityCliTool|IUnityCliContext|ToolDescriptor|ContextDescriptor/);
  assert.doesNotMatch(completion, /"editor\.state"/);
});

test("private editor-state snapshot is explicitly registered but absent from public inventories", async () => {
  const [registry, sampler, capability, coordinator, instanceRegistry, http, bridge] = await Promise.all([
    readFile(join(editor, "Bridge", "BridgeCommandRegistry.cs"), "utf8"),
    readFile(join(editor, "Infrastructure", "UnityCliEditorStateSnapshot.cs"), "utf8"),
    readFile(join(editor, "Infrastructure", "UnityCliJson.cs"), "utf8"),
    readFile(join(editor, "Infrastructure", "UnityCliOperationCoordinator.cs"), "utf8"),
    readFile(join(editor, "Infrastructure", "UnityCliInstanceRegistry.cs"), "utf8"),
    readFile(join(editor, "Bridge", "BridgeHttpServer.cs"), "utf8"),
    readFile(join(editor, "UnityCliBridge.cs"), "utf8"),
  ]);

  assert.match(registry, /RegisterEditorStateSnapshotCommand\(registry\)/);
  assert.match(sampler, new RegExp(`registry\\.Register<UnityCli\\.Protocol\\.BridgeRequest>\\(\\s*"${escape(privateCommand)}"`));
  assert.match(sampler, /SampleEditorStateSnapshot\(\)/);
  assert.match(sampler, /"schemaVersion"/);
  assert.match(sampler, /"observedAt"/);
  assert.match(sampler, /"sourceEditorSessionId"/);
  assert.match(sampler, /"sourceListenerGeneration"/);
  assert.match(sampler, /"fields"/);
  const snapshotMethod = sampler.slice(sampler.indexOf("SampleEditorStateSnapshot"), sampler.indexOf("RegisterEditorStateSnapshotCommand"));
  assert.doesNotMatch(snapshotMethod, /"progress"/);
  assert.doesNotMatch(snapshotMethod, /"blockers"/);
  assert.match(sampler, /"snapshot", SampleEditorStateSnapshot\(\)/);
  assert.match(sampler, /"progress", SnapshotProgress\(\)/);
  assert.match(sampler, /"blockers", new List<object>\(\)/);
  assert.match(coordinator, /if \(IsEditorStateSnapshotCommand\(command\)\) return true;/);
  assert.match(instanceRegistry, /"lastUnitySnapshot", SampleEditorStateSnapshot\(now\)/, "Registry publication must contain only the private sampler result");
  assert.match(instanceRegistry, /"progress", SnapshotProgress\(\)/, "publication progress must remain a session field");
  assert.match(instanceRegistry, /"blockers", new List<object>\(\)/, "publication blockers must remain a session field");
  assert.match(http, /var allowedDuringCompileOrImport =[\s\S]*IsEditorStateSnapshotCommand\(request\.Command\)/);
  assert.match(http, /var allowedDuringPlayModeTransition =[\s\S]*IsEditorStateSnapshotCommand\(request\.Command\)/);
  assert.doesNotMatch(http, /x-unity-cli-invocation-origin|internalOrigin|invocationOrigin/);
  assert.match(bridge, /var allowedDuringCompileOrImport =[\s\S]*IsEditorStateSnapshotCommand\(request\.Command\)/);
  assert.match(bridge, /var allowedDuringPlayModeTransition =[\s\S]*IsEditorStateSnapshotCommand\(request\.Command\)/);
  assert.match(bridge, /OnBeforeAssemblyReload[\s\S]*?StopListener\(\);/, "domain reload must close the listener for natural fallback");
  assert.doesNotMatch(capability, new RegExp(escape(privateCommand)));
});

function escape(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
