import assert from "node:assert/strict";
import test from "node:test";
import { readFile, readdir } from "node:fs/promises";
import { bridgeCommandRegistry } from "../../src/bridge/index.js";
import { builtinToolMetadata, allRegisteredToolMetadata } from "../../src/tools/metadata/index.js";
import { commandContracts, validateBridgeRegistryContracts, validateToolMetadataContracts } from "../../src/contracts/command-contract.js";
import "../../src/extensions/index.js";

const editorUrl = new URL("../../../unitypkg/Editor/", import.meta.url);
const extensionsUrl = new URL("../../../extensions/", import.meta.url);
const filledCsharpHandlerCommands = [
  "scene.active",
  "scene.build-settings",
  "scene.create",
  "scene.load",
  "scene.save",
  "go.inspect",
  "go.create",
  "go.modify",
  "go.duplicate",
  "go.move-relative",
  "go.look-at",
  "go.delete",
  "component.add",
  "component.remove",
  "component.set",
  "editor.undo",
  "editor.redo",
  "editor.resolution",
  "project.tag",
  "project.layer",
  "physics.collision-matrix",
  "physics.material",
  "physics.joint",
  "physics.force",
  "physics.overlap",
  "physics.rigidbody",
  "animation.clip.create",
  "animation.clip.modify",
  "scriptable-object.create",
  "scriptable-object.info",
  "scriptable-object.list-types",
  "scriptable-object.modify"
];

test("command contract validates bridge registry and metadata", () => {
  // Full validation includes extension contributions registered via extensions/index.js.
  assert.doesNotThrow(() => validateBridgeRegistryContracts(bridgeCommandRegistry));
 assert.doesNotThrow(() => validateToolMetadataContracts(allRegisteredToolMetadata(), bridgeCommandRegistry));
  // All registered tools (core + extension) + runtime-tool commands + project-tool commands + context commands + extension command
  assert.equal(commandContracts().length, allRegisteredToolMetadata().length + 4 + 3 + 1);
  // Bridge registry includes core + extension specs
  const bridgeSpecCount = new Set(bridgeCommandRegistry.specs().map((spec) => spec.bridgeCommand)).size;
  assert.ok(bridgeSpecCount > 0, "Bridge registry should have specs");
});

test("command contract includes Profiler bridge commands", () => {
  const contracts = commandContracts().filter((entry) => entry.group === "profiler");
  assert.deepEqual(contracts.map((entry) => entry.bridgeCommand), ["profiler.areas", "profiler.capture", "profiler.clear", "profiler.counter", "profiler.deep", "profiler.export", "profiler.frame", "profiler.frame-debugger.disable", "profiler.frame-debugger.enable", "profiler.frame-debugger.events", "profiler.hierarchy", "profiler.load", "profiler.memory", "profiler.memory-snapshot.compare", "profiler.memory-snapshot.list", "profiler.memory-snapshot.take", "profiler.module-enable", "profiler.modules", "profiler.object-memory", "profiler.pause", "profiler.rendering-stats", "profiler.script-stats", "profiler.start", "profiler.status", "profiler.stop"]);
});

test("command contract marks asset.refresh as bridge-job capable", () => {
  const contract = commandContracts().find((entry) => entry.bridgeCommand === "asset.refresh");
  assert.ok(contract, "asset.refresh should be in command contract");
  assert.equal(contract.handler_type, "hybrid");
  assert.equal(contract.csharp_handler, "present");
  assert.equal(contract.destructive, false);
});

test("command contract marks editor.compile as live bridge handler", () => {
  const contract = commandContracts().find((entry) => entry.bridgeCommand === "editor.compile");
  assert.ok(contract, "editor.compile should be in command contract");
  assert.equal(contract.handler_type, "bridge");
  assert.equal(contract.csharp_handler, "present");
  assert.equal(contract.destructive, false);
});

test("command contract marks editor.compile-diagnostics as CLI orchestration", () => {
  const contract = commandContracts().find((entry) => entry.bridgeCommand === "editor.compile-diagnostics");
  assert.ok(contract, "editor.compile-diagnostics should be in command contract");
  assert.equal(contract.handler_type, "hybrid");
  assert.equal(contract.csharp_handler, "none");
  assert.equal(contract.destructive, false);
});

test("command contract marks C# handler gaps as present", async () => {
  const contracts = new Map(commandContracts().map((entry) => [entry.bridgeCommand, entry]));
  const csharpCommands = await registeredCsharpCommands(editorUrl);
  const extensionCsharpCommands = await registeredExtensionCsharpCommands();
  const allCsharpCommands = new Set([...csharpCommands, ...extensionCsharpCommands]);

  for (const command of filledCsharpHandlerCommands) {
    assert.equal(contracts.get(command)?.csharp_handler, "present", `${command} contract should be present`);
    assert.ok(allCsharpCommands.has(command), `${command} should be registered in C# BridgeCommandRegistry or an extension bundle`);
  }

  assert.equal(csharpCommands.size, 233);
  assert.equal(extensionCsharpCommands.size, 70);
  assert.equal(allCsharpCommands.size, 303);
});

async function registeredCsharpCommands(directory: URL): Promise<Set<string>> {
  const commands = new Set<string>();
  for (const file of await listCSharpFiles(directory)) {
    // 排除 Samples(样板工具不参与核心契约)
    if (file.pathname.includes("/Samples/") || file.pathname.includes("\\Samples\\")) continue;
    const source = await readFile(file, "utf8");
    // RegisterCommand 形态(旧)
    for (const match of source.matchAll(/RegisterCommand\(registry, "([^"]+)"/g)) {
      commands.add(match[1]);
    }
    // IUnityCliTool 形态(新,Stage 2+):文件含 IUnityCliTool< 则提取 Name="..."
    if (source.includes(": IUnityCliTool<") || source.includes(": IUnityCliContext<")) {
      for (const match of source.matchAll(/Name\s*=\s*"([a-z][a-z0-9-]*(?:\.[a-z0-9-]+)+)"/g)) {
        commands.add(match[1]);
      }
    }
  }
  return commands;
}

async function registeredExtensionCsharpCommands(): Promise<Set<string>> {
  const commands = new Set<string>();
  const extensionDirs = await readdir(extensionsUrl, { withFileTypes: true });
  for (const entry of extensionDirs) {
    if (!entry.isDirectory()) continue;
    const extensionEditorUrl = new URL(`${entry.name}/unitypkg/Editor/`, extensionsUrl);
    for (const command of await registeredCsharpCommands(extensionEditorUrl)) {
      commands.add(command);
    }
  }
  return commands;
}

async function listCSharpFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCSharpFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}
