import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import test from "node:test";
import { handleToolsCommand } from "../src/tools/commands.js";

interface InventoryEntry {
  name: string;
  group: string;
  descriptorOwner: string;
  destructiveHint: boolean;
  requiresPackages: string[];
  source: string;
}

interface CompletionEntry {
  toolName: string;
  descriptorOwner: string;
}

interface CommandEntry {
  bridgeCommand: string;
  group: string;
  destructive: boolean;
  requires_packages?: string[];
}

const OFFLINE_INSTANCE = `unity-${"f".repeat(64)}`;
const STATIC_CATALOG_PATHS = [
  "contracts/static-capability-catalog.json",
  "src/static-capability-catalog.ts",
  "scripts/capture-static-capability-catalog.mjs",
  "scripts/audit-static-capability-catalog.mjs",
];

async function canonicalInventory(): Promise<InventoryEntry[]> {
  const moduleUrl = pathToFileURL(join(process.cwd(), "scripts", "lib", "public-tool-inventory.mjs")).href;
  const module = await import(moduleUrl) as { collectPublicToolInventory(root: string): InventoryEntry[] };
  return module.collectPublicToolInventory(process.cwd());
}

test("canonical C# source inventory exactly drives completion and command contracts", async () => {
  const inventory = (await canonicalInventory()).filter((tool) => tool.descriptorOwner === "built-in" || tool.descriptorOwner.startsWith("extension:"));
  const completion = JSON.parse(await readFile("contracts/tool-completion-contract.json", "utf8")) as { tools: CompletionEntry[] };
  const commands = JSON.parse(await readFile("contracts/command-contract.json", "utf8")) as { commands: CommandEntry[] };
  const completionNames = completion.tools
    .filter((tool) => tool.descriptorOwner === "built-in" || tool.descriptorOwner.startsWith("extension:"))
    .map((tool) => tool.toolName)
    .sort();
  const inventoryNames = inventory.map((tool) => tool.name).sort();

  assert.deepEqual(completionNames, inventoryNames);
  assert.equal(new Set(inventoryNames).size, inventoryNames.length);
  const commandByName = new Map(commands.commands.map((command) => [command.bridgeCommand, command]));
  for (const tool of inventory) {
    assert.ok(tool.group, `${tool.name}.group`);
    const command = commandByName.get(tool.name);
    assert.ok(command, `${tool.name} command contract`);
    assert.equal(command!.group, tool.group, `${tool.name}.group`);
    assert.equal(command!.destructive, tool.destructiveHint, `${tool.name}.destructive`);
    assert.deepEqual(command!.requires_packages ?? [], tool.requiresPackages, `${tool.name}.requiresPackages`);
  }
});

test("duplicate static catalog files and consumers stay deleted", async () => {
  for (const path of STATIC_CATALOG_PATHS) assert.equal(existsSync(path), false, path);
  const files = [
    "package.json",
    "scripts/gen-command-contract.mjs",
    "scripts/gen-tool-completion-registry.mjs",
    "scripts/run-final-live-matrix.mjs",
    "src/contracts/command-contract.ts",
    "src/tools/catalog.ts",
  ];
  const source = (await Promise.all(files.map((path) => readFile(path, "utf8")))).join("\n");
  assert.doesNotMatch(source, /static-capability-catalog|staticCapabilityCatalog|staticBridgeToolMetadata|static-catalog:/);
});

test("canonical Tools retain every approved duplicate successor", async () => {
  const names = new Set((await canonicalInventory()).map((tool) => tool.name));
  for (const name of ["prefab.hierarchy", "prefab.info", "camera.list", "scene.list-opened", "profiler.rendering-stats"])
    assert.equal(names.has(name), true, `${name} must remain the canonical Tool`);
});

test("session-mutating read-shaped Tools retain explicit side effects in canonical descriptors", async () => {
  const expected: Record<string, string[]> = {
    "asset.preview": ["asset-preview-generation", "editor-cache-write"],
    "debug.console-clear": ["editor-console-state"],
    "editor.active-tool-set": ["editor-tool-state"],
    "editor.redo": ["undo-redo-replay", "undo-stack"],
    "editor.selection-set": ["editor-selection"],
    "editor.undo": ["undo-redo-replay", "undo-stack"],
    "lighting.bake-cancel": ["lightmapping-bake-cancel"],
    "rendering.scene-debug": ["scene-view-state"],
    "profiler.clear": ["profiler-data-clear"],
    "profiler.deep": ["profiler-session-state"],
    "profiler.frame": ["frame-timing-capture"],
    "profiler.frame-debugger-disable": ["frame-debugger-state"],
    "profiler.frame-debugger-enable": ["frame-debugger-state", "editor-window-state"],
    "profiler.load": ["profiler-loaded-data-session", "profiler-recording-stop"],
    "profiler.module-enable": ["profiler-area-state"],
    "profiler.pause": ["profiler-session-state"],
    "profiler.start": ["profiler-session-state"],
    "profiler.stop": ["profiler-session-state"],
    "game-object.move-to-scene": ["scene-modify"],
    "scene.view-frame": ["editor-selection", "scene-view-state"],
    "ui.modify-visual-element": ["ui-document-session-state"],
  };
  const inventory = new Map((await canonicalInventory()).map((tool) => [tool.name, tool]));
  for (const [name, sideEffects] of Object.entries(expected)) {
    const tool = inventory.get(name);
    assert.ok(tool, name);
    const source = await readFile(tool!.source, "utf8");
    for (const effect of sideEffects) assert.match(source, new RegExp(`"${effect}"`), `${name}.${effect}`);
  }
});

test("public Tool discovery rejects an offline explicit instance without a descriptor fallback", async () => {
  const runtime = { dataDir: "Z:/definitely-missing-unity-cli-data" };
  const flags = { instance: OFFLINE_INSTANCE };
  for (const [command, args] of [["group", []], ["search", ["hierarchy"]], ["describe", ["scene.hierarchy"]]] as const) {
    const result = await handleToolsCommand(command, [...args], flags, runtime, `req_offline_${command}`);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "E_INSTANCE_NOT_FOUND");
  }
});
