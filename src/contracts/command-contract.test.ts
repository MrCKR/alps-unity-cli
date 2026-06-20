import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import test from "node:test";
import { bridgeCommandRegistry } from "../bridge/index.js";
import { builtinToolMetadata } from "../tools/metadata/index.js";
import { commandContracts, validateBridgeRegistryContracts, validateToolMetadataContracts } from "./command-contract.js";

const editorUrl = new URL("../../unitypkg/Editor/", import.meta.url);
const filledCsharpHandlerCommands = [
  "scene.active",
  "scene.build-settings",
  "scene.create",
  "scene.load",
  "scene.save",
  "go.inspect",
  "go.create",
  "go.modify",
  "go.delete",
  "component.add",
  "component.remove",
  "component.set",
  "script.create",
  "script.read",
  "script.edit",
  "script.validate"
];

test("command contract validates bridge registry and metadata", () => {
  assert.doesNotThrow(() => validateBridgeRegistryContracts(bridgeCommandRegistry));
  assert.doesNotThrow(() => validateToolMetadataContracts(builtinToolMetadata(), bridgeCommandRegistry));
  // Built-in tool metadata count + runtime-tool commands + project-tool commands + context commands
  // (runtime-tool.{list,run} + project-tool.{list,run} + context.{read,list} are bridge commands without tool metadata)
  assert.equal(commandContracts().length, builtinToolMetadata().length + 6);
  assert.equal(new Set(bridgeCommandRegistry.specs().map((spec) => spec.bridgeCommand)).size, 149);
});

test("command contract marks Profiler frozen coverage as architecture-sensitive", () => {
  const contracts = commandContracts().filter((entry) => entry.group === "profiler");
  assert.deepEqual(contracts.map((entry) => entry.bridgeCommand), ["profiler.capture", "profiler.counter", "profiler.export", "profiler.frame", "profiler.memory", "profiler.pause", "profiler.start", "profiler.status", "profiler.stop"]);
  for (const contract of contracts) {
    assert.equal(contract.architectureSensitive, true, `${contract.bridgeCommand} should be architecture sensitive`);
    assert.equal(contract.frozenDecision, "ALP-165-Q4", `${contract.bridgeCommand} should reference Q4 frozen decision`);
    assert.deepEqual(contract.coverageScope, ["session", "counter", "memory", "frame-debugger", "export"], `${contract.bridgeCommand} should declare frozen Profiler scope`);
  }
});

test("command contract marks ALP-306 C# handler gaps as present", async () => {
  const contracts = new Map(commandContracts().map((entry) => [entry.bridgeCommand, entry]));
  const csharpCommands = await registeredCsharpCommands();

  for (const command of filledCsharpHandlerCommands) {
    assert.equal(contracts.get(command)?.csharp_handler, "present", `${command} contract should be present`);
    assert.ok(csharpCommands.has(command), `${command} should be registered in C# BridgeCommandRegistry`);
  }

  assert.equal(csharpCommands.size, 147);
});

async function registeredCsharpCommands(): Promise<Set<string>> {
  const commands = new Set<string>();
  for (const file of await listCSharpFiles(editorUrl)) {
    const source = await readFile(file, "utf8");
    for (const match of source.matchAll(/RegisterCommand\(registry, "([^"]+)"/g)) {
      commands.add(match[1]);
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
