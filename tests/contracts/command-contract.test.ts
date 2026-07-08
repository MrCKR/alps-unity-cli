import assert from "node:assert/strict";
import test from "node:test";
import { readFile, readdir } from "node:fs/promises";
import { bridgeCommandRegistry } from "../../src/bridge/index.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../../src/extensions/index.js";
import { commandContracts, validateBridgeRegistryContracts, validateExtensionManifestContracts, validateToolMetadataContracts } from "../../src/contracts/command-contract.js";
import { allRegisteredToolMetadata, builtinToolMetadata, extensionToolMetadata } from "../../src/tools/metadata/index.js";
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

test("command contract validates TS-local metadata, bridge registry and extension manifests under the new split model", () => {
  assert.equal(builtinToolMetadata().length, 34, "TS-local built-in metadata count should stay stable");
  assert.equal(allRegisteredToolMetadata().length, 34, "allRegisteredToolMetadata should be TS-local only");
  assert.equal(extensionToolMetadata().length, 0, "offline extension tool metadata should stay empty");

  assert.doesNotThrow(() => validateBridgeRegistryContracts(bridgeCommandRegistry));
  assert.doesNotThrow(() => validateToolMetadataContracts(allRegisteredToolMetadata(), bridgeCommandRegistry));
  assert.doesNotThrow(() => validateExtensionManifestContracts(BUILTIN_EXTENSION_MANIFESTS, bridgeCommandRegistry, extensionToolMetadata()));

  assert.equal(commandContracts().length, 335, "contract should still expose the full tool catalog");
});

test("command contract keeps representative TS-local, C# built-in, and extension entries", () => {
  const contracts = new Map(commandContracts().map((entry) => [entry.bridgeCommand, entry]));

  assert.equal(contracts.get("tools.group")?.handler_type, "offline");
  assert.equal(contracts.get("build.run")?.handler_type, "job");
  assert.equal(contracts.get("scene.hierarchy")?.handler_type, "bridge");
  assert.equal(contracts.get("probuilder.ping")?.handler_type, "bridge");
  assert.equal(contracts.get("cinemachine.ping")?.group, "cinemachine");
});

test("command contract includes Profiler bridge commands", () => {
  const contracts = commandContracts().filter((entry) => entry.group === "profiler");
  assert.deepEqual(contracts.map((entry) => entry.bridgeCommand), [
    "profiler.areas",
    "profiler.capture",
    "profiler.clear",
    "profiler.counter",
    "profiler.deep",
    "profiler.export",
    "profiler.frame",
    "profiler.frame-debugger.disable",
    "profiler.frame-debugger.enable",
    "profiler.frame-debugger.events",
    "profiler.hierarchy",
    "profiler.load",
    "profiler.memory",
    "profiler.memory-snapshot.compare",
    "profiler.memory-snapshot.list",
    "profiler.memory-snapshot.take",
    "profiler.module-enable",
    "profiler.modules",
    "profiler.object-memory",
    "profiler.pause",
    "profiler.rendering-stats",
    "profiler.script-stats",
    "profiler.start",
    "profiler.status",
    "profiler.stop"
  ]);
});

test("command contract marks representative routing correctly", () => {
  const assetRefresh = commandContracts().find((entry) => entry.bridgeCommand === "asset.refresh");
  const editorCompile = commandContracts().find((entry) => entry.bridgeCommand === "editor.compile");
  const compileDiagnostics = commandContracts().find((entry) => entry.bridgeCommand === "editor.compile-diagnostics");

  assert.ok(assetRefresh);
  assert.equal(assetRefresh.handler_type, "hybrid");
  assert.equal(assetRefresh.csharp_handler, "present");
  assert.equal(assetRefresh.destructive, false);

  assert.ok(editorCompile);
  assert.equal(editorCompile.handler_type, "bridge");
  assert.equal(editorCompile.csharp_handler, "present");

  assert.ok(compileDiagnostics);
  assert.equal(compileDiagnostics.handler_type, "hybrid");
  assert.equal(compileDiagnostics.csharp_handler, "none");
});

test("registered C# command counts remain stable", async () => {
  const contracts = new Map(commandContracts().map((entry) => [entry.bridgeCommand, entry]));
  const csharpCommands = await registeredCsharpCommands(editorUrl);
  const extensionCsharpCommands = await registeredExtensionCsharpCommands();
  const allCsharpCommands = new Set([...csharpCommands, ...extensionCsharpCommands]);

  for (const command of filledCsharpHandlerCommands) {
    assert.equal(contracts.get(command)?.csharp_handler, "present", `${command} contract should be present`);
    assert.ok(allCsharpCommands.has(command), `${command} should be registered in C# or extension bundles`);
  }

  assert.equal(csharpCommands.size, 233);
  assert.equal(extensionCsharpCommands.size, 70);
  assert.equal(allCsharpCommands.size, 303);
});

async function registeredCsharpCommands(directory: URL): Promise<Set<string>> {
  const commands = new Set<string>();
  for (const file of await listCSharpFiles(directory)) {
    if (file.pathname.includes("/Samples/") || file.pathname.includes("\\Samples\\")) continue;
    const source = await readFile(file, "utf8");
    for (const match of source.matchAll(/RegisterCommand\(registry, "([^"]+)"/g)) {
      commands.add(match[1]);
    }
    if (source.includes(": IUnityCliTool<") || source.includes(": IUnityCliDeferredTool<")) {
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
