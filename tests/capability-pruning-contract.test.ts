import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { staticCapabilityCatalog } from "../src/static-capability-catalog.js";
import { allRegisteredToolMetadata, findToolByName } from "../src/tools/metadata/index.js";

interface InventoryEntry {
  name: string;
  descriptorOwner: string;
  source: string;
}

interface SchemaNode {
  $ref?: string;
  $defs?: Record<string, SchemaNode>;
  properties?: Record<string, SchemaNode>;
  required?: string[];
  type?: string;
}

interface CanonicalDescriptor {
  name: string;
  group: string;
  inputSchema: SchemaNode;
  outputSchema: SchemaNode;
  destructiveHint: boolean;
  supportsDryRun: boolean;
  sideEffects?: string[];
  playModeSupport: string;
  target: string;
}

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

test("all 18 removed identities are absent from canonical source and every generated contract input", async () => {
  assert.equal(REMOVED_TOOLS.length, 18);
  assert.equal(new Set(REMOVED_TOOLS).size, 18);

  const inventory = await publicToolInventory();
  const inventoryNames = new Set(inventory.map((entry) => entry.name));
  for (const name of REMOVED_TOOLS) assert.equal(inventoryNames.has(name), false, `source inventory: ${name}`);

  const staticNames = uniqueNames(staticCapabilityCatalog().tools.map((entry) => String(entry.name ?? "")), "static capability catalog");
  const csharpSourceNames = uniqueNames(inventory
    .filter((entry) => entry.descriptorOwner === "built-in" || entry.descriptorOwner.startsWith("extension:"))
    .map((entry) => entry.name), "canonical C# source inventory");
  assert.deepEqual([...staticNames].sort(), [...csharpSourceNames].sort(), "static catalog must be an exact source snapshot");
  for (const name of REMOVED_TOOLS) assert.equal(staticNames.has(name), false, `static catalog: ${name}`);

  const completion = JSON.parse(await readFile("contracts/tool-completion-contract.json", "utf8")) as { tools: Array<{ toolName: string }> };
  const completionNames = uniqueNames(completion.tools.map((entry) => entry.toolName), "completion contract");
  assert.deepEqual([...completionNames].sort(), [...inventoryNames].sort(), "completion contract must cover the canonical public inventory exactly");
  for (const name of REMOVED_TOOLS) assert.equal(completionNames.has(name), false, `completion contract: ${name}`);

  const commandContract = JSON.parse(await readFile("contracts/command-contract.json", "utf8")) as { commands: Array<{ bridgeCommand: string }> };
  const commandNames = new Set(commandContract.commands.map((entry) => entry.bridgeCommand));
  for (const name of REMOVED_TOOLS) assert.equal(commandNames.has(name), false, `command contract: ${name}`);

  for (const [group, command] of REMOVED_ROUTES) assert.equal(bridgeCommandRegistry.lookup(group, command), undefined, `${group}.${command}`);
  const localNames = new Set(allRegisteredToolMetadata().map((tool) => tool.name));
  for (const name of REMOVED_TOOLS) assert.equal(localNames.has(name), false, `TS metadata: ${name}`);

  const generator = await readFile("scripts/gen-command-contract.mjs", "utf8");
  assert.match(generator, /assertExactNameSet\(sourceDescriptorNames, staticDescriptorNames\)/);
  assert.doesNotMatch(generator, /publicToolNames|!publicToolNames\.has/);

  const source = (await Promise.all((await nonGeneratedSourceFiles()).map((path) => readFile(path, "utf8")))).join("\n");
  for (const name of REMOVED_TOOLS) assert.equal(source.includes(name), false, `non-generated source residual: ${name}`);
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

test("replacement mutations expose fresh readback without narrowing reflection input", async () => {
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
  assert.doesNotMatch(reflectionCall, /\[UnityCliRequired\]\s*public List<string> ParameterTypes/);
  assert.match(reflectionSupport, /parameterTypeNames != null/);
  assert.match(reflectionSupport, /argumentCount/);
  assert.match(reflectionSupport, /E_METHOD_ARGUMENT_REQUIRED/);
  assert.match(reflectionSupport, /E_METHOD_COMPLETION_UNPROVABLE/);
  assert.match(reflectionSupport, /parameter\.ParameterType\.IsByRef \|\| parameter\.IsOut/);
});

test("retained schemas and metadata come from canonical descriptor owners", async () => {
  const inventoryByName = new Map((await publicToolInventory()).map((entry) => [entry.name, entry]));
  const descriptors = canonicalDescriptors();
  for (const name of REPLACEMENTS) {
    assert.ok(inventoryByName.get(name)?.source, `canonical owner: ${name}`);
    const descriptor = descriptors.get(name);
    assert.ok(descriptor, `descriptor input: ${name}`);
    assert.ok(descriptor.inputSchema, `input schema: ${name}`);
    assert.ok(descriptor.outputSchema, `output schema: ${name}`);
    assert.equal(descriptor.playModeSupport, EXPECTED_PLAY_MODE.get(name), `PlayMode: ${name}`);
    assert.equal(descriptor.target, "editor", `target: ${name}`);
  }

  for (const name of ["build.scene.add", "build.scene.remove", "build.scene.enable", "build.scene.disable"]) {
    const descriptor = requiredDescriptor(descriptors, name);
    assert.deepEqual(descriptor.sideEffects, ["project-settings-write"], name);
    assert.equal(descriptor.destructiveHint, true, name);
    assert.equal(descriptor.supportsDryRun, true, name);
    assert.ok(descriptor.inputSchema.required?.includes("path"), name);
  }

  const quality = requiredDescriptor(descriptors, "graphics.quality");
  assert.deepEqual(quality.sideEffects, ["settings-write"]);
  assertProperties(quality.inputSchema, ["qualityLevel", "vSyncCount", "antiAliasing", "shadowDistance", "masterTextureLimit", "lodBias", "maximumLODLevel"]);

  const pipeline = requiredDescriptor(descriptors, "graphics.pipeline-settings");
  assert.deepEqual(pipeline.sideEffects, ["settings-write", "asset-import", "session-affecting"]);
  assertProperties(pipeline.inputSchema, ["renderPipeline"]);

  const lightProbe = requiredDescriptor(descriptors, "graphics.light-probe-group");
  assert.deepEqual(lightProbe.sideEffects, ["scene-modify"]);
  assertProperties(lightProbe.inputSchema, ["target", "create", "positions"]);
  assertProperties(lightProbe.outputSchema, ["target", "probeCount", "positions", "plannedEffects"]);

  const reflectionProbe = requiredDescriptor(descriptors, "graphics.reflection-probe");
  assert.deepEqual(reflectionProbe.sideEffects, ["scene-modify"]);
  assertProperties(reflectionProbe.outputSchema, ["target", "probe", "plannedEffects"]);

  const raycast = requiredDescriptor(descriptors, "physics.raycast");
  assert.equal(raycast.destructiveHint, false);
  assert.equal(raycast.sideEffects, undefined);
  assertProperties(raycast.inputSchema, ["mode", "originX", "originY", "originZ", "directionX", "directionY", "directionZ", "distance", "layerMask", "all"]);
  assertProperties(raycast.outputSchema, ["query", "hit", "hitCount", "hits", "nextActions"]);

  const reflection = requiredDescriptor(descriptors, "reflection.method.call");
  assert.equal(reflection.destructiveHint, true);
  assert.equal(reflection.supportsDryRun, true);
  assert.deepEqual(reflection.sideEffects, ["arbitrary-method-invocation"]);
  assertProperties(reflection.inputSchema, ["typeName", "methodName", "args", "parameterTypes", "instanceRef", "completionProbe"]);
  const reflectionRequired = new Set(reflection.inputSchema.required ?? []);
  assert.equal(reflectionRequired.has("parameterTypes"), false, "parameterTypes remains optional");
  for (const name of ["typeName", "methodName", "completionProbe"]) assert.equal(reflectionRequired.has(name), true, name);
});

test("console and hierarchy replacements depend on ISSUE-042 bounded continuation schemas", () => {
  const descriptors = canonicalDescriptors();
  for (const [name, pageBound] of [["console.get", "count"], ["scene.hierarchy", "pageSize"]] as const) {
    const descriptor = requiredDescriptor(descriptors, name);
    assertProperties(descriptor.inputSchema, [pageBound, "cursor"]);
    const pageInfo = referencedProperty(descriptor.outputSchema, "pageInfo");
    assertProperties(pageInfo, ["nextCursor", "truncated"]);
    const pageProperties = schemaProperties(pageInfo);
    assert.equal(pageProperties.total, undefined, `${name} must not claim an unbounded total`);
    assert.equal(pageProperties.totalCount, undefined, `${name} must not claim an unbounded totalCount`);
  }
});

test("transition allowlists drop removed public diagnostics while retaining the bypass interface", async () => {
  const [http, bridge, admission] = await Promise.all([
    readFile("unitypkg/Editor/Bridge/BridgeHttpServer.cs", "utf8"),
    readFile("unitypkg/Editor/UnityCliBridge.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs", "utf8"),
  ]);
  assert.doesNotMatch(http, /request\.Command == "editor\.console"|request\.Command == "diagnose\.preflight"/);
  assert.doesNotMatch(bridge, /request\.Command == "editor\.console"|request\.Command == "diagnose\.preflight"/);
  assert.match(admission, /private static bool AdmissionBypassCommand\(string command\)/);
  assert.doesNotMatch(admission, /case "diagnose\.preflight"/);
});

test("retained TS package roots are live EditMode operations, not offline inspection wrappers", () => {
  for (const name of ["package.install", "package.remove", "package.resolve", "package.deploy", "package.restore"]) {
    const tool = findToolByName(name);
    assert.ok(tool, name);
    assert.equal(tool.handler, "hybrid", name);
    assert.equal(tool.playModeSupport, "editor-only", name);
    assert.match(tool.description, /EditMode only/i, name);
  }
});

/** 复用生成器的 canonical inventory，而不是在测试中重写 source 识别规则。 */
async function publicToolInventory(): Promise<InventoryEntry[]> {
  const moduleUrl = pathToFileURL(join(process.cwd(), "scripts", "lib", "public-tool-inventory.mjs")).href;
  const inventoryModule = await import(moduleUrl) as { collectPublicToolInventory(root: string): InventoryEntry[] };
  return inventoryModule.collectPublicToolInventory(process.cwd());
}

/** C# descriptor 使用 static generator input，TS descriptor 使用 defineTool registry 真源。 */
function canonicalDescriptors(): Map<string, CanonicalDescriptor> {
  const descriptors = new Map<string, CanonicalDescriptor>();
  for (const descriptor of staticCapabilityCatalog().tools) descriptors.set(String(descriptor.name), descriptor as unknown as CanonicalDescriptor);
  for (const descriptor of allRegisteredToolMetadata()) descriptors.set(descriptor.name, descriptor as unknown as CanonicalDescriptor);
  return descriptors;
}

function requiredDescriptor(descriptors: Map<string, CanonicalDescriptor>, name: string): CanonicalDescriptor {
  const descriptor = descriptors.get(name);
  assert.ok(descriptor, name);
  return descriptor;
}

function uniqueNames(names: string[], owner: string): Set<string> {
  assert.equal(names.every((name) => name.length > 0), true, `${owner} contains an empty name`);
  const result = new Set(names);
  assert.equal(result.size, names.length, `${owner} contains duplicate names`);
  return result;
}

function schemaProperties(schema: SchemaNode): Record<string, SchemaNode> {
  return schema.properties ?? {};
}

function assertProperties(schema: SchemaNode, expected: readonly string[]): void {
  const properties = schemaProperties(schema);
  for (const property of expected) assert.ok(properties[property], property);
}

/** 解析 static schema 内的本地 $defs 引用。 */
function referencedProperty(schema: SchemaNode, property: string): SchemaNode {
  const node = schemaProperties(schema)[property];
  assert.ok(node, property);
  if (!node.$ref) return node;
  const prefix = "#/$defs/";
  assert.equal(node.$ref.startsWith(prefix), true, node.$ref);
  const resolved = schema.$defs?.[node.$ref.slice(prefix.length)];
  assert.ok(resolved, node.$ref);
  return resolved;
}

async function nonGeneratedSourceFiles(): Promise<string[]> {
  const roots = ["src", join("unitypkg", "Editor"), "extensions"];
  const files = (await Promise.all(roots.map((root) => sourceFiles(root)))).flat();
  return files.filter((path) => !path.includes(".generated-") && !path.includes(join("Editor", "Generated")));
}

async function sourceFiles(root: string): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) result.push(...await sourceFiles(path));
    else if (entry.isFile() && (entry.name.endsWith(".ts") || entry.name.endsWith(".cs"))) result.push(path);
  }
  return result;
}
