import { existsSync } from "node:fs";
import { mkdir, writeFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";

process.env.UNITY_CLI_GENERATING_CONTRACT = "1";

const distRoot = existsSync("dist/bridge/index.js") ? "dist" : "dist/src";
const { bridgeCommandRegistry } = await importFromDist("bridge/index.js");
const { allRegisteredToolMetadata } = await importFromDist("tools/metadata/index.js");
await importFromDist("extensions/index.js");

const OUTPUT_PATH = "contracts/command-contract.json";
const DEFAULT_TIMEOUT_MS = 30000;

const bridgeOnlyEntries = new Map([
  ["context.describe", { handler_type: "bridge", readOnly: true, destructive: false }],
  ["context.list", { handler_type: "bridge", readOnly: true, destructive: false }],
  ["context.read", { handler_type: "bridge", readOnly: true, destructive: false }],
  ["extension.status", { handler_type: "hybrid", readOnly: true, destructive: false }],
  ["project-tool.list", { handler_type: "bridge", readOnly: true, destructive: false }],
  ["project-tool.run", { handler_type: "bridge", readOnly: false, destructive: false }],
  ["runtime-tool.list", { handler_type: "bridge", readOnly: true, destructive: false }],
  ["runtime-tool.run", { handler_type: "bridge", readOnly: false, destructive: true }]
]);

const entries = new Map();

for (const tool of allRegisteredToolMetadata()) {
  entries.set(tool.name, {
    bridgeCommand: tool.name,
    group: tool.group,
    command: commandName(tool.name, tool.group),
    handler_type: normalizeHandler(tool.handler),
    csharp_handler: csharpHandlerForTool(tool),
    readOnly: tool.readOnlyHint,
    destructive: tool.destructiveHint,
    timeout: tool.timeoutMs ?? DEFAULT_TIMEOUT_MS,
    ...(tool.requiresPackages?.length ? { requires_packages: [...tool.requiresPackages] } : {})
  });
}

for (const spec of bridgeCommandRegistry.specs()) {
  const key = spec.toolName ?? spec.bridgeCommand;
  if (entries.has(key)) continue;
  const override = bridgeOnlyEntries.get(spec.bridgeCommand);
  if (!override) continue;
  entries.set(key, {
    bridgeCommand: key,
    group: spec.group,
    command: spec.command || commandName(key, spec.group),
    handler_type: override.handler_type,
    csharp_handler: "present",
    readOnly: override.readOnly,
    destructive: override.destructive,
    timeout: DEFAULT_TIMEOUT_MS
  });
}

const commands = [...entries.values()].sort((left, right) => left.bridgeCommand.localeCompare(right.bridgeCommand));

await mkdir("contracts", { recursive: true });
await writeFile(OUTPUT_PATH, `${JSON.stringify({
  version: 1,
  generatedFrom: "unified C#/TS/metadata command contract",
  commands
}, null, 2)}\n`, "utf8");

console.log(JSON.stringify({ ok: true, output: OUTPUT_PATH, commands: commands.length }, null, 2));

function importFromDist(relativePath) {
  return import(pathToFileURL(`${distRoot}/${relativePath}`).href);
}

function normalizeHandler(handler) {
  if (handler === "bridge-runtime") return "bridge";
  return handler ?? "bridge";
}

function csharpHandlerForTool(tool) {
  const handler = normalizeHandler(tool.handler);
  const hasBridgeSpec = bridgeCommandRegistry.specs().some((spec) => (spec.toolName ?? spec.bridgeCommand) === tool.name);
  return (handler === "bridge" || handler === "hybrid") && hasBridgeSpec && tool.group !== "tools" ? "present" : "none";
}

function commandName(name, group) {
  return name.startsWith(`${group}.`) ? name.slice(group.length + 1) : name;
}
