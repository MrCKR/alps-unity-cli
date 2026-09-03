import assert from "node:assert/strict";
import test from "node:test";
import { readFile, readdir } from "node:fs/promises";
import { bridgeCommandRegistry } from "../../src/bridge/index.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../../src/extensions/index.js";
import { PRIVATE_BRIDGE_COMMANDS, commandContracts, validateBridgeRegistryContracts, validateExtensionManifestContracts, validateToolMetadataContracts } from "../../src/contracts/command-contract.js";
import { extractCSharpRegisteredCommandsFromSource } from "../../src/contracts/csharp-command-extractor.js";
import { allRegisteredToolMetadata, builtinToolMetadata } from "../../src/tools/metadata/index.js";
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
  "project.tag.add",
  "project.tag.remove",
  "project.layer.add",
  "project.layer.remove",
  "physics.collision-matrix",
  "physics.material",
  "physics.joint.add",
  "physics.joint.remove",
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

test("command contract inventory agrees with metadata, bridge registry and extension manifests", () => {
  const localMetadata = builtinToolMetadata();
  const extensionMetadata: never[] = [];
  const allMetadata = allRegisteredToolMetadata();
  const contracts = commandContracts();

  assert.deepEqual(new Set(allMetadata.map((tool) => tool.name)), new Set(localMetadata.map((tool) => tool.name)));
  assert.equal(new Set(contracts.map((entry) => entry.bridgeCommand)).size, contracts.length, "contract bridge commands must be unique");
  assert.doesNotThrow(() => validateBridgeRegistryContracts(bridgeCommandRegistry));
  assert.doesNotThrow(() => validateToolMetadataContracts(allMetadata, bridgeCommandRegistry));
  assert.doesNotThrow(() => validateExtensionManifestContracts(BUILTIN_EXTENSION_MANIFESTS, bridgeCommandRegistry, extensionMetadata));
});

test("command contract keeps representative TS-local, C# built-in, and extension entries", () => {
  const contracts = new Map(commandContracts().map((entry) => [entry.bridgeCommand, entry]));

  assert.equal(contracts.get("tools.group"), undefined);
  assert.equal(contracts.get("extension.status"), undefined);
  assert.equal(contracts.get("project-tool.health")?.handler_type, "bridge");
  assert.equal(contracts.get("project-tool.summary")?.handler_type, "bridge");
  assert.equal(contracts.get("runtime-tool.describe")?.handler_type, "bridge");
  assert.equal(contracts.get("build.run")?.handler_type, "hybrid");
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
  assert.equal(assetRefresh.csharp_handler, "none");
  assert.equal(assetRefresh.destructive, false);

  assert.ok(editorCompile);
  assert.equal(editorCompile.handler_type, "hybrid");
  assert.equal(editorCompile.csharp_handler, "none");

  assert.equal(compileDiagnostics, undefined);
});

test("typed generic C# registry registrations are extracted from executable code", () => {
  const source = `
    registry.Register<UnityCli.Protocol.BridgeRequest>(
      "project-tool.run", "project-tool", false, 30000, ProjectToolRun);
    RegisterCommand(registry, "runtime-tool.list", "runtime-tool", false, RuntimeToolList);
  `;

  assert.deepEqual(extractCSharpRegisteredCommandsFromSource(source), ["project-tool.run", "runtime-tool.list"]);
});

test("missing typed generic C# registry registration is reported by extraction", () => {
  const source = `registry.Register<UnityCli.Protocol.BridgeRequest>("project-tool.run", "project-tool", false, 60000, ProjectToolRun);`;

  assert.deepEqual(extractCSharpRegisteredCommandsFromSource(source.replace("Register<UnityCli.Protocol.BridgeRequest>", "RemovedRegister")), []);
});

test("C# registry extraction ignores similar comments and strings", () => {
  const source = `
    // registry.Register<UnityCli.Protocol.BridgeRequest>("runtime-tool.run", "runtime-tool", false, 60000, RuntimeToolRun);
    var example = "RegisterCommand(registry, \\\"project-tool.run\\\")";
    /* RegisterCommand(registry, "project-tool.run", "project-tool", false, ProjectToolRun); */
  `;

  assert.deepEqual(extractCSharpRegisteredCommandsFromSource(source), []);
});

test("registered C# command inventory agrees with command contracts", async () => {
  // editor.state 是 TS-owned Tool；Unity 只注册不可发现的私有快照命令。
  assert.deepEqual([...PRIVATE_BRIDGE_COMMANDS].sort(), ["internal.asset.refresh.start", "internal.asset.refresh.status", "internal.build.run.start", "internal.doctor.business", "internal.doctor.catalog", "internal.doctor.compile", "internal.editor.compile.start", "internal.editor.compile.status", "internal.editor.refresh.start", "internal.editor.refresh.status", "internal.editor.state.snapshot", "internal.navmesh.bake.cancel", "internal.navmesh.bake.plan", "internal.navmesh.bake.snapshot", "internal.navmesh.bake.start", "internal.navmesh.bake.status", "internal.package.embed.start", "internal.package.embed.status", "internal.package.files.claim", "internal.package.files.commit", "internal.package.files.fail", "internal.package.files.prove", "internal.package.resolve.start", "internal.package.resolve.status", "internal.test.run.start", "internal.test.run.status"]);
  const contracts = new Map(commandContracts().map((entry) => [entry.bridgeCommand, entry]));
  const csharpCommands = await registeredCsharpCommands(editorUrl);
  const extensionCsharpCommands = await registeredExtensionCsharpCommands();
  const allCsharpCommands = new Set([...csharpCommands, ...extensionCsharpCommands]);

  for (const command of filledCsharpHandlerCommands) {
    assert.equal(contracts.get(command)?.csharp_handler, "present", `${command} contract should be present`);
    assert.ok(allCsharpCommands.has(command), `${command} should be registered in C# or extension bundles`);
  }

  for (const command of allCsharpCommands) {
    if (PRIVATE_BRIDGE_COMMANDS.has(command)) {
      assert.equal(contracts.has(command), false, `${command} must remain private and absent from the public contract`);
      continue;
    }
    const contract = contracts.get(command);
    assert.ok(contract, `${command} is registered in C# but absent from the command contract inventory`);
    assert.equal(contract.csharp_handler, "present", `${command} must advertise its C# handler`);
  }
  // Compile coordinator 的 private handlers 随 C# backend 独立交付；其唯一 TS contract
  // 约束是绝不能成为 public command contract 项。
});

async function registeredCsharpCommands(directory: URL): Promise<Set<string>> {
  const commands = new Set<string>();
  for (const file of await listCSharpFiles(directory)) {
    const source = await readFile(file, "utf8");
    for (const command of extractCSharpRegisteredCommandsFromSource(source)) {
      commands.add(command);
    }
    if (source.includes(": IUnityCliTool<")) {
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
