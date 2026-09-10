import { existsSync } from "node:fs";
import { mkdir, writeFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";
import { collectPublicToolInventory } from "./lib/public-tool-inventory.mjs";

process.env.UNITY_CLI_GENERATING_CONTRACT = "1";

const distRoot = existsSync("dist/bridge/index.js") ? "dist" : "dist/src";
const { bridgeCommandRegistry } = await importFromDist("bridge/index.js");
const { allRegisteredToolMetadata } = await importFromDist("tools/metadata/index.js");
await importFromDist("extensions/index.js");

const OUTPUT_PATH = "contracts/command-contract.json";
const DEFAULT_TIMEOUT_MS = 30000;
const publicToolInventory = collectPublicToolInventory(process.cwd());

const bridgeOnlyEntries = new Map([
  ["catalog.editor.health", { handler_type: "bridge", destructive: false }],
  ["catalog.editor.groups", { handler_type: "bridge", destructive: false }],
  ["catalog.editor.summary", { handler_type: "bridge", destructive: false }],
  ["catalog.editor.describe", { handler_type: "bridge", destructive: false }],
  ["catalog.editor.list", { handler_type: "bridge", destructive: false }],
  ["catalog.editor.run", { handler_type: "bridge", destructive: false }],
  ["catalog.player.groups", { handler_type: "bridge", destructive: false }],
  ["catalog.player.summary", { handler_type: "bridge", destructive: false }],
  ["catalog.player.describe", { handler_type: "bridge", destructive: false }],
  ["catalog.player.list", { handler_type: "bridge", destructive: false }],
  ["catalog.player.run", { handler_type: "bridge", destructive: false }]
]);

const entries = new Map();

for (const tool of allRegisteredToolMetadata()) {
  entries.set(tool.name, {
    bridgeCommand: tool.name,
    group: tool.group,
    command: commandName(tool.name, tool.group),
    handler_type: normalizeHandler(tool.handler),
    csharp_handler: csharpHandlerForTool(tool),
    destructive: tool.destructiveHint,
    timeout: tool.timeoutMs ?? DEFAULT_TIMEOUT_MS,
    ...(tool.requiresPackages?.length ? { requires_packages: [...tool.requiresPackages] } : {})
  });
}

for (const tool of publicToolInventory) {
  if (tool.descriptorOwner === "ts-local" || entries.has(tool.name)) continue;
  entries.set(tool.name, {
    bridgeCommand: tool.name,
    group: tool.group,
    command: commandName(tool.name, tool.group),
    handler_type: "bridge",
    csharp_handler: "present",
    destructive: tool.destructiveHint,
    timeout: DEFAULT_TIMEOUT_MS,
    ...(tool.requiresPackages.length ? { requires_packages: [...tool.requiresPackages] } : {})
  });
}

for (const spec of bridgeCommandRegistry.specs()) {
  const key = spec.toolName ?? spec.bridgeCommand;
  if (entries.has(key)) continue;

  const override = spec.toolName ? undefined : bridgeOnlyEntries.get(spec.bridgeCommand);
  if (spec.internal && !override) continue;
  if (override) {
    entries.set(key, {
      bridgeCommand: key,
      group: spec.group,
      command: spec.command || commandName(key, spec.group),
      handler_type: override.handler_type,
      csharp_handler: "present",
      destructive: override.destructive,
      timeout: DEFAULT_TIMEOUT_MS
    });
    continue;
  }

  // TS-local metadata 与 Bridge specs 共同提供公开 command contract 来源。
  entries.set(key, {
    bridgeCommand: key,
    group: spec.group,
    command: spec.command || commandName(key, spec.group),
    handler_type: normalizeSpecHandler(spec),
    csharp_handler: "present",
    destructive: spec.destructiveHint ?? false,
    timeout: DEFAULT_TIMEOUT_MS
  });
}

const commands = [...entries.values()].sort((left, right) => left.bridgeCommand.localeCompare(right.bridgeCommand));

await mkdir("contracts", { recursive: true });
await writeFile(OUTPUT_PATH, `${JSON.stringify({
  version: 1,
  generatedFrom: "canonical C#/TS source inventory and metadata",
  commands
}, null, 2)}\n`, "utf8");

console.log(JSON.stringify({ ok: true, output: OUTPUT_PATH, commands: commands.length }, null, 2));

function importFromDist(relativePath) {
  return import(pathToFileURL(`${distRoot}/${relativePath}`).href);
}

/** 保留descriptor的真实dispatcher owner，禁止把Player transport降级为Editor Bridge。 */
function normalizeHandler(handler) {
  return handler ?? "bridge";
}

function normalizeSpecHandler(spec) {
  return "bridge";
}

function csharpHandlerForTool(tool) {
  const handler = normalizeHandler(tool.handler);
  const hasBridgeSpec = bridgeCommandRegistry.specs().some((spec) => (spec.toolName ?? spec.bridgeCommand) === tool.name);
  return (handler === "bridge" || handler === "hybrid") && hasBridgeSpec && tool.group !== "tools" ? "present" : "none";
}

function commandName(name, group) {
  return name.startsWith(`${group}.`) ? name.slice(group.length + 1) : name;
}
