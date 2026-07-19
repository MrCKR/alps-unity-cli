import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../../unitypkg/Editor/", import.meta.url);
const contextsUrl = new URL("Contexts/", editorUrl);
const registryUrl = new URL("ProjectContexts/ProjectContextRegistry.cs", editorUrl);
const registeredContextUrl = new URL("ProjectContexts/RegisteredContext.cs", editorUrl);
const interfaceUrl = new URL("ProjectTools/IUnityCliTool.cs", editorUrl);

const expectedContexts = [
  "project.info", "project.tags", "project.layers",
  "editor.selection", "editor.windows", "editor.active-tool", "editor.tool-states", "editor.menu-items",
  "prefab.stage", "prefab.info", "prefab.hierarchy",
  "scene.opened", "scene.cameras", "scene.gameobject", "scene.gameobject.components", "scene.gameobject.component",
  "scene.volumes", "scene.renderer-features", "scene.rendering-stats",
  "tests.list", "instances.list"
] as const;

const forbiddenWriteTokens = [
  "ExecuteMenuItem", "SaveAssets", "AssetDatabase.Refresh", "isPlaying =", "EditorSceneManager.SaveScene",
  "MarkSceneDirty", "PackageManager.", "File.Write", "File.Append", "Directory.Create", "Directory.Delete", "Process.Start"
] as const;

test("context routes are non-mutating and context.read uses the typed registry path", async () => {
  const registry = await readFile(registryUrl, "utf8");

  assert.match(registry, /RegisterCommand\(registry, "context\.list", "context", false/);
  assert.match(registry, /RegisterCommand\(registry, "context\.describe", "context", false/);
  assert.match(registry, /registry\.Register<UnityCli\.Protocol\.BridgeRequest>\("context\.read", "context", false/);
  assert.match(registry, /\[UnityCliAnyJson\] public object Input \{ get; set; \}/);
  assert.match(registry, /UnityCliSerializer\.Deserialize\(inputJson, definition\.RequestType\)/);
  assert.match(registry, /await definition\.ReadAsync\(typedInput,[\s\S]*cancellationToken\)/);
});

test("all Unity-owned built-in contexts are self-contained typed IUnityCliContext implementations", async () => {
  const sources = await readContextSources();
  const combined = sources.map(({ source }) => source).join("\n");

  for (const name of expectedContexts) {
    assert.match(combined, new RegExp(`Name\\s*=\\s*"${escapeRegex(name)}"`), `${name} descriptor must be registered`);
  }

  for (const { path, source } of sources) {
    if (!source.includes("IUnityCliContext<")) continue;
    const match = source.match(/class\s+(\w+)\s*:\s*IUnityCliContext<\1\.Request,\s*\1\.Result>/);
    assert.ok(match, `${path}: context must own typed Request and Result DTOs`);
    assert.match(source, /ReadAsync\(Request request, UnityCliProjectToolContext ctx,[\s\S]*CancellationToken ct = default\)/, `${path}: context must expose the unified async read signature`);
    assert.doesNotMatch(source, /RawJsonArg\(/, `${path}: context must not bypass typed input/output`);
  }
});

test("context registry has one RegisteredContext model with Type-derived schemas", async () => {
  const [registry, registered, interfaceSource] = await Promise.all([
    readFile(registryUrl, "utf8"), readFile(registeredContextUrl, "utf8"), readFile(interfaceUrl, "utf8")
  ]);

  assert.match(interfaceSource, /interface IUnityCliContext<TReq, TRes>/);
  assert.match(interfaceSource, /ValueTask<TRes> ReadAsync\(TReq request, UnityCliProjectToolContext ctx, CancellationToken ct = default\)/);
  assert.match(registered, /internal sealed class RegisteredContext/);
  assert.match(registered, /public Type RequestType/);
  assert.match(registered, /public Type ResultType/);
  assert.match(registered, /Func<object, UnityCliProjectToolContext, CancellationToken, ValueTask<object>> ReadAsync/);
  assert.match(registry, /Dictionary<string, RegisteredContext>/);
  assert.match(registry, /UnityCliSchemaDeriver\.DeriveSchema\(typeof\(TReq\)\)/);
  assert.match(registry, /UnityCliSchemaDeriver\.DeriveSchema\(typeof\(TRes\)\)/);
});

test("context registry cannot register an empty or untyped context", async () => {
  const registry = await readFile(registryUrl, "utf8");
  assert.match(registry, /definition == null \|\| string\.IsNullOrEmpty\(definition\.Name\) \|\| string\.IsNullOrEmpty\(definition\.Description\)/);
  assert.match(registry, /definition\.ReadAsync == null \|\| definition\.RequestType == null \|\| definition\.ResultType == null/);
  assert.match(registry, /Duplicate context registration/);
});

test("context implementations preserve the read-only boundary", async () => {
  for (const { path, source } of await readContextSources()) {
    if (!source.includes("IUnityCliContext<")) continue;
    for (const token of forbiddenWriteTokens) {
      assert.doesNotMatch(source, new RegExp(escapeRegex(token)), `${path}: context must not use write API ${token}`);
    }
  }
});

test("context descriptors retain schema-relevant DTOs and availability diagnostics", async () => {
  const combined = (await readContextSources()).map(({ source }) => source).join("\n");
  assert.match(combined, /\[UnityCliRequired\][\s\S]*public string Path \{ get; set; \}/);
  assert.match(combined, /public sealed class PageInfo/);
  assert.match(combined, /Available\s*=\s*false/);
  assert.match(combined, /Diagnostics\s*=/);
  assert.match(combined, /NextActions\s*=/);
  assert.match(combined, /Name\s*=\s*"scene\.renderer-features"[\s\S]*RequiresPackages\s*=\s*new\[\]\s*\{\s*"com\.unity\.render-pipelines\.universal"/);
});

test("active-tool context never emits non-finite JSON numbers", async () => {
  const source = await readFile(new URL("editor/active-tool.cs", contextsUrl), "utf8");
  assert.match(source, /public bool HandleAvailable/);
  assert.match(source, /IsFinite\(handlePosition\) && IsFinite\(handleRotation\)/);
  assert.match(source, /handleAvailable \? handlePosition : Vector3\.zero/);
  assert.match(source, /handleAvailable \? handleRotation : Quaternion\.identity/);
  assert.match(source, /!float\.IsNaN\(value\) && !float\.IsInfinity\(value\)/);
});

test("selection context returns reusable object detail without mutating selection", async () => {
  const source = await readFile(new URL("editor/selection.cs", contextsUrl), "utf8");
  assert.match(source, /ActiveObjectDetail/);
  assert.match(source, /ObjectDetails/);
  assert.match(source, /GameObjectDetails/);
  assert.match(source, /InstanceId/);
  assert.match(source, /AssetPath/);
  assert.match(source, /HierarchyPath/);
  assert.doesNotMatch(source, /Selection\.activeObject\s*=/);
});

async function readContextSources(): Promise<Array<{ path: string; source: string }>> {
  const files = await listCSharpFiles(contextsUrl);
  return Promise.all(files.map(async (file) => ({ path: file.pathname, source: await readFile(file, "utf8") })));
}

async function listCSharpFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) return listCSharpFiles(child);
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return nested.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
