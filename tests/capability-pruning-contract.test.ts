import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { handleToolsCommand, type ToolsRuntime } from "../src/tools/commands.js";
import { allRegisteredToolMetadata, findToolByName } from "../src/tools/metadata/index.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";

const REMOVED_TOOLS = [
  "instances.list", "editor.console", "scene.build-settings", "go.inspect", "scene.data", "tests.list",
  "graphics.settings", "graphics.pipeline-quality", "graphics.probe-positions", "graphics.stats-counters",
  "graphics.stats-memory", "physics.raycast-all", "package.list", "package.status", "package.info",
  "package.registry.get", "package.scoped-registry.list", "package.search",
] as const;

const REMOVED_ROUTES = [
  ["editor", "console"], ["scene", "build-settings"], ["go", "inspect"], ["scene", "data"],
  ["graphics", "settings"], ["graphics", "pipeline-quality"], ["graphics", "probe-positions"],
  ["graphics", "stats-counters"], ["graphics", "stats-memory"], ["physics", "raycast-all"],
] as const;

const replacementTools = [
  makeLiveTool({ name: "build.scene.list", summary: "List Build Settings scenes.", description: "Read Build Settings scenes in Edit or Play mode.", group: "build", playModeSupport: "supported" }),
  ...["add", "remove", "enable", "disable"].map((action) => makeLiveTool({ name: `build.scene.${action}`, summary: `${action} one Build Settings scene.`, description: `${action} one exact Build Settings scene with fresh readback.`, group: "build", destructiveHint: true, supportsDryRun: true, sideEffects: ["project-settings-write"], playModeSupport: "supported" })),
  makeLiveTool({ name: "console.get", summary: "Read bounded Console pages.", description: "Read bounded Console pages with filters and cursor continuation.", group: "console", playModeSupport: "supported" }),
  makeLiveTool({ name: "console.clear", summary: "Clear Console entries.", description: "Clear Console entries with count readback.", group: "console", supportsDryRun: true, sideEffects: ["editor-console-state"], playModeSupport: "supported" }),
  makeLiveTool({ name: "scene.gameobject", summary: "Read one GameObject.", description: "Read one GameObject after bounded discovery.", group: "scene", playModeSupport: "supported" }),
  makeLiveTool({ name: "scene.hierarchy", summary: "Read a bounded hierarchy page.", description: "Read a bounded hierarchy page with numeric continuation.", group: "scene", playModeSupport: "supported" }),
  makeLiveTool({ name: "go.find", summary: "Find GameObjects in bounded pages.", description: "Find GameObjects with a bounded page size and cursor.", group: "go", playModeSupport: "supported" }),
  makeLiveTool({ name: "graphics.quality", summary: "Read or write QualitySettings.", description: "Dedicated QualitySettings read/write with fresh readback.", group: "graphics", destructiveHint: true, sideEffects: ["settings-write"], playModeSupport: "session-affecting" }),
  makeLiveTool({ name: "graphics.pipeline-settings", summary: "Read or write the default render pipeline.", description: "Dedicated render pipeline write with path and GUID readback.", group: "graphics", destructiveHint: true, sideEffects: ["settings-write", "asset-import"], playModeSupport: "session-affecting" }),
  makeLiveTool({ name: "graphics.light-probe-group", summary: "Read or write one LightProbeGroup.", description: "Dedicated LightProbeGroup positions with fresh readback.", group: "graphics", sideEffects: ["scene-modify"], playModeSupport: "supported" }),
  makeLiveTool({ name: "graphics.reflection-probe", summary: "Read or write one ReflectionProbe.", description: "Dedicated ReflectionProbe settings with fresh readback.", group: "graphics", sideEffects: ["scene-modify"], playModeSupport: "supported" }),
  makeLiveTool({ name: "graphics.stats", summary: "Read unified render stats.", description: "Read one bounded counter and memory snapshot.", group: "graphics", playModeSupport: "supported" }),
  makeLiveTool({ name: "physics.raycast", summary: "Raycast one or all hits.", description: "Use all=true for every sorted 2D or 3D hit.", group: "physics", playModeSupport: "supported" }),
  makeLiveTool({
    name: "reflection.method.call", summary: "Call one exact synchronous overload.",
    description: "Requires exact parameterTypes and an independent completionProbe.", group: "reflection",
    destructiveHint: true, supportsDryRun: true, sideEffects: ["arbitrary-method-invocation"], playModeSupport: "session-affecting",
    inputSchema: { type: "object", required: ["typeName", "methodName", "parameterTypes", "completionProbe"], properties: { typeName: { type: "string" }, methodName: { type: "string" }, parameterTypes: { type: "array" }, completionProbe: { type: "object" } } },
  }),
];
const runtime: ToolsRuntime = { projectTools: replacementTools };

const REPLACEMENTS = [
  "editor.state", "console.get", "console.clear",
  "build.scene.list", "build.scene.add", "build.scene.remove", "build.scene.enable", "build.scene.disable",
  "scene.gameobject", "scene.hierarchy", "go.find", "test.run", "test.cancel",
  "graphics.quality", "graphics.pipeline-settings", "graphics.light-probe-group", "graphics.reflection-probe", "graphics.stats",
  "physics.raycast", "package.install", "package.remove", "package.resolve", "package.deploy", "package.restore",
  "reflection.method.call",
] as const;

const EXPECTED_PLAY_MODE = new Map<string, string>();
for (const name of ["editor.state", "console.get", "console.clear", "build.scene.list", "build.scene.add", "build.scene.remove", "build.scene.enable", "build.scene.disable", "scene.gameobject", "scene.hierarchy", "go.find", "test.cancel", "graphics.light-probe-group", "graphics.reflection-probe", "graphics.stats", "physics.raycast"]) EXPECTED_PLAY_MODE.set(name, "supported");
for (const name of ["test.run", "graphics.quality", "graphics.pipeline-settings", "reflection.method.call"]) EXPECTED_PLAY_MODE.set(name, "session-affecting");
for (const name of ["package.install", "package.remove", "package.resolve", "package.deploy", "package.restore"]) EXPECTED_PLAY_MODE.set(name, "editor-only");

test("removed capabilities have no canonical registration, route, or completion contract", async () => {
  for (const name of REMOVED_TOOLS) assert.equal(findToolByName(name), undefined, name);
  for (const [group, command] of REMOVED_ROUTES) assert.equal(bridgeCommandRegistry.lookup(group, command), undefined, `${group}.${command}`);

  const completion = JSON.parse(await readFile("contracts/tool-completion-contract.json", "utf8")) as { tools: Array<{ toolName: string }> };
  const commandGenerator = await readFile("scripts/gen-command-contract.mjs", "utf8");
  assert.match(commandGenerator, /collectPublicToolInventory/);
  assert.match(commandGenerator, /!publicToolNames\.has\(name\)/);
  const completionNames = new Set(completion.tools.map((entry) => entry.toolName));
  for (const name of REMOVED_TOOLS) assert.equal(completionNames.has(name), false, name);

  const source = (await Promise.all((await sourceFiles("src")).filter((path) => !path.includes(".generated-")).map((path) => readFile(path, "utf8"))))
    .concat(await Promise.all((await sourceFiles("unitypkg/Editor")).filter((path) => !path.includes(`${join("Editor", "Generated")}`)).map((path) => readFile(path, "utf8"))))
    .join("\n");
  for (const name of REMOVED_TOOLS) assert.equal(source.includes(name), false, name);
});

test("replacement completion contracts preserve timeout and recovery ownership", async () => {
  const completion = JSON.parse(await readFile("contracts/tool-completion-contract.json", "utf8")) as { tools: Array<{ toolName: string; executionClass: string; timeoutError: string; proofSource: { kind: string }; reloadPolicy: { kind: string } }> };
  const byName = new Map(completion.tools.map((entry) => [entry.toolName, entry]));
  for (const name of REPLACEMENTS) {
    const contract = byName.get(name);
    assert.ok(contract, name);
    assert.equal(contract.timeoutError, "E_EXECUTION_TIMEOUT", name);
    assert.ok(contract.proofSource.kind, name);
    assert.ok(contract.reloadPolicy.kind, name);
  }
  for (const name of ["package.install", "package.remove", "package.resolve", "package.deploy", "package.restore"]) assert.equal(byName.get(name)?.executionClass, "external", name);
  assert.equal(byName.get("test.run")?.executionClass, "resumable");
  assert.equal(byName.get("reflection.method.call")?.executionClass, "observed-state");
});

test("replacement mutations expose fresh readback and fail before unsafe mode or signature effects", async () => {
  const [buildState, buildAdd, buildRemove, consoleClear, packageLive, reflectionCall, reflectionSupport] = await Promise.all([
    readFile("unitypkg/Editor/Tools/build/BuildSceneStateTool.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/build/scene/add.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/build/scene/remove.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/console/clear.cs", "utf8"),
    readFile("src/tools/package/live-resolve.ts", "utf8"),
    readFile("unitypkg/Editor/Tools/reflection/method/call.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliReflection.cs", "utf8"),
  ]);
  assert.match(buildState, /Scene = BuildEntry\(scenes\[index\], index\)/);
  assert.match(buildState, /E_BUILD_SCENE_STATE_MISMATCH/);
  assert.match(buildAdd, /PlayModeSupport = "supported"[\s\S]*Scene = observedIndex < 0 \? null : BuildSceneStateTool\.BuildEntry/);
  assert.match(buildRemove, /PlayModeSupport = "supported"[\s\S]*E_BUILD_SCENE_REMOVE_MISMATCH/);
  assert.match(consoleClear, /PreviousCount = previousCount, RemainingCount = remainingCount/);
  assert.match(consoleClear, /E_CONSOLE_CLEAR_STATE_MISMATCH/);
  assert.match(packageLive, /E_TOOL_PLAYMODE_UNAVAILABLE[\s\S]*playModeSupport: "editor-only"/);
  assert.ok(packageLive.indexOf("E_TOOL_PLAYMODE_UNAVAILABLE") < packageLive.indexOf("context.projectPath = state.identity.canonicalProjectPath"));
  assert.match(packageLive, /E_EXECUTION_TIMEOUT[\s\S]*Inspect Package Manager and package files/);
  assert.match(reflectionCall, /\[UnityCliRequired\][\s\S]*List<string> ParameterTypes/);
  assert.match(reflectionSupport, /E_METHOD_SIGNATURE_REQUIRED/);
  assert.match(reflectionSupport, /E_METHOD_ARGUMENT_REQUIRED/);
  assert.match(reflectionSupport, /E_METHOD_COMPLETION_UNPROVABLE/);
  assert.match(reflectionSupport, /parameter\.ParameterType\.IsByRef \|\| parameter\.IsOut/);
});

test("replacement families are discoverable through group, search, and self-contained describe", async () => {
  const groups = await handleToolsCommand("group", [], {}, runtime, "req_pruning_groups");
  assert.equal(groups.ok, true, JSON.stringify(groups));
  const groupNames = new Set((groups.data as { groups: Array<{ name: string }> }).groups.map((entry) => entry.name));
  for (const group of ["editor", "console", "build", "scene", "go", "test", "graphics", "physics", "package", "reflection"]) assert.equal(groupNames.has(group), true, group);
  assert.equal(groupNames.has("instances"), false);
  assert.equal(groupNames.has("tests"), false);

  const search = await handleToolsCommand("search", [REPLACEMENTS.join("|")], { "page-size": "100" }, runtime, "req_pruning_search");
  assert.equal(search.ok, true, JSON.stringify(search));
  const searchNames = new Set((search.data as { tools: Array<{ name: string }> }).tools.map((entry) => entry.name));
  for (const name of REPLACEMENTS) assert.equal(searchNames.has(name), true, name);

  for (const name of REPLACEMENTS) {
    const described = await handleToolsCommand("describe", [name], {}, runtime, `req_pruning_${name.replaceAll(".", "_")}`);
    assert.equal(described.ok, true, `${name}: ${JSON.stringify(described)}`);
    const data = described.data as { name: string; inputSchema: unknown; playModeSupport: string; target: string };
    assert.equal(data.name, name);
    assert.ok(data.inputSchema);
    assert.equal(data.playModeSupport, EXPECTED_PLAY_MODE.get(name), name);
    assert.equal(data.target, "editor", name);
  }
});

test("retained TS package roots are live EditMode operations, not offline inspection wrappers", () => {
  for (const name of ["package.install", "package.remove", "package.resolve", "package.deploy", "package.restore"]) {
    const tool = findToolByName(name);
    assert.ok(tool, name);
    assert.equal(tool.handler, "hybrid", name);
    assert.equal(tool.playModeSupport, "editor-only", name);
    assert.match(tool.description, /EditMode only/i, name);
  }
  const localNames = new Set(allRegisteredToolMetadata().map((tool) => tool.name));
  for (const name of REMOVED_TOOLS.filter((entry) => entry.startsWith("package."))) assert.equal(localNames.has(name), false, name);
});

async function sourceFiles(root: string): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) result.push(...await sourceFiles(path));
    else if (entry.isFile() && (entry.name.endsWith(".ts") || entry.name.endsWith(".cs"))) result.push(path);
  }
  return result;
}
