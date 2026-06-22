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
  "script.create",
  "script.read",
  "script.hash",
  "script.edit",
  "script.apply-edits",
  "script.validate",
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
  "scriptable-object.create",
  "scriptable-object.info",
  "scriptable-object.modify"
];

test("command contract validates bridge registry and metadata", () => {
  // Full validation includes extension contributions registered via extensions/index.js.
  assert.doesNotThrow(() => validateBridgeRegistryContracts(bridgeCommandRegistry));
 assert.doesNotThrow(() => validateToolMetadataContracts(allRegisteredToolMetadata(), bridgeCommandRegistry));
  // All registered tools (core + extension) + runtime-tool commands + project-tool commands + context commands + extension commands
  assert.equal(commandContracts().length, allRegisteredToolMetadata().length + 4 + 3 + 1 + 2);
  // Bridge registry includes core + extension specs
  const bridgeSpecCount = new Set(bridgeCommandRegistry.specs().map((spec) => spec.bridgeCommand)).size;
  assert.ok(bridgeSpecCount > 0, "Bridge registry should have specs");
});

test("command contract includes Profiler bridge commands", () => {
  const contracts = commandContracts().filter((entry) => entry.group === "profiler");
  assert.deepEqual(contracts.map((entry) => entry.bridgeCommand), ["profiler.capture", "profiler.counter", "profiler.export", "profiler.frame", "profiler.memory", "profiler.pause", "profiler.start", "profiler.status", "profiler.stop"]);
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

  assert.equal(csharpCommands.size, 159);
  assert.equal(extensionCsharpCommands.size, 31);
  assert.equal(allCsharpCommands.size, 190);
});

async function registeredCsharpCommands(directory: URL): Promise<Set<string>> {
  const commands = new Set<string>();
  for (const file of await listCSharpFiles(directory)) {
    const source = await readFile(file, "utf8");
    for (const match of source.matchAll(/RegisterCommand\(registry, "([^"]+)"/g)) {
      commands.add(match[1]);
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
