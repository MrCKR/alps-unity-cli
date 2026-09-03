import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findToolByName } from "../src/tools/metadata/index.js";

interface InventoryEntry {
  name: string;
  descriptorOwner: string;
  source: string;
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

test("all 18 removed identities are absent from canonical source and runtime routing", async () => {
  assert.equal(REMOVED_TOOLS.length, 18);
  assert.equal(new Set(REMOVED_TOOLS).size, 18);

  const inventory = await publicToolInventory();
  const inventoryNames = new Set(inventory.map((entry) => entry.name));
  assert.equal(inventoryNames.size, inventory.length, "canonical public inventory contains duplicate names");
  for (const name of REMOVED_TOOLS) assert.equal(inventoryNames.has(name), false, `source inventory: ${name}`);

  for (const [group, command] of REMOVED_ROUTES) {
    assert.equal(bridgeCommandRegistry.lookup(group, command), undefined, `${group}.${command}`);
  }

  const generator = await readFile("scripts/gen-command-contract.mjs", "utf8");
  assert.match(generator, /assertExactNameSet\(sourceDescriptorNames, staticDescriptorNames\)/);
  assert.doesNotMatch(generator, /publicToolNames|!publicToolNames\.has/);

  const source = (await Promise.all((await nonGeneratedSourceFiles()).map((path) => readFile(path, "utf8")))).join("\n");
  for (const name of REMOVED_TOOLS) assert.equal(source.includes(name), false, `non-generated source residual: ${name}`);
});

test("replacement mutations expose fresh readback for the removed duplicate surfaces", async () => {
  const [buildState, buildAdd, buildRemove, consoleClear, packageLive] = await Promise.all([
    readFile("unitypkg/Editor/Tools/build/BuildSceneStateTool.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/build/scene/add.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/build/scene/remove.cs", "utf8"),
    readFile("unitypkg/Editor/Tools/console/clear.cs", "utf8"),
    readFile("src/tools/package/live-resolve.ts", "utf8"),
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
});

test("transition allowlists retain diagnose.preflight while dropping editor.console", async () => {
  const [http, bridge, admission] = await Promise.all([
    readFile("unitypkg/Editor/Bridge/BridgeHttpServer.cs", "utf8"),
    readFile("unitypkg/Editor/UnityCliBridge.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs", "utf8"),
  ]);
  assert.doesNotMatch(http, /request\.Command == "editor\.console"/);
  assert.match(http, /request\.Command == "diagnose\.preflight"/);
  assert.doesNotMatch(bridge, /request\.Command == "editor\.console"/);
  assert.match(bridge, /request\.Command == "diagnose\.preflight"/);
  assert.match(admission, /private static bool AdmissionBypassCommand\(string command\)/);
  assert.match(admission, /case "diagnose\.preflight"/);
});

test("retained TS package roots are live EditMode operations without stale readSnapshot imports", async () => {
  for (const name of ["package.install", "package.remove", "package.resolve", "package.deploy", "package.restore"]) {
    const tool = findToolByName(name);
    assert.ok(tool, name);
    assert.equal(tool.handler, "hybrid", name);
    assert.equal(tool.playModeSupport, "editor-only", name);
    assert.match(tool.description, /EditMode only/i, name);
  }

  const registrySources = await Promise.all([
    readFile("src/tools/package/registry-clear.ts", "utf8"),
    readFile("src/tools/package/registry-set.ts", "utf8"),
    readFile("src/tools/package/scoped-registry-add.ts", "utf8"),
  ]);
  for (const source of registrySources) assert.doesNotMatch(source, /import\s*\{[^}]*\breadSnapshot\b/);
});

/** 复用生成器的 canonical inventory，而不是在测试中重写 source 识别规则。 */
async function publicToolInventory(): Promise<InventoryEntry[]> {
  const moduleUrl = pathToFileURL(join(process.cwd(), "scripts", "lib", "public-tool-inventory.mjs")).href;
  const inventoryModule = await import(moduleUrl) as { collectPublicToolInventory(root: string): InventoryEntry[] };
  return inventoryModule.collectPublicToolInventory(process.cwd());
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
