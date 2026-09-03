import { existsSync } from "node:fs";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";
import { collectPublicToolInventory } from "./lib/public-tool-inventory.mjs";

process.env.UNITY_CLI_GENERATING_CONTRACT = "1";

const distRoot = existsSync("dist/bridge/index.js") ? "dist" : "dist/src";
const { bridgeCommandRegistry } = await importFromDist("bridge/index.js");
const { allRegisteredToolMetadata } = await importFromDist("tools/metadata/index.js");
const staticCatalog = JSON.parse(await readFile("contracts/static-capability-catalog.json", "utf8"));
const sourceDescriptorNames = collectPublicToolInventory(process.cwd())
  .filter((tool) => tool.descriptorOwner === "built-in" || tool.descriptorOwner.startsWith("extension:"))
  .map((tool) => tool.name);
const staticDescriptorNames = descriptorNames(staticCatalog.tools);
assertExactNameSet(sourceDescriptorNames, staticDescriptorNames);
await importFromDist("extensions/index.js");

const OUTPUT_PATH = "contracts/command-contract.json";
const DEFAULT_TIMEOUT_MS = 30000;
const publicToolNames = new Set(collectPublicToolInventory(process.cwd()).map((entry) => entry.name));

const bridgeOnlyEntries = new Map([
  ["project-tool.health", { handler_type: "bridge", destructive: false }],
  ["project-tool.groups", { handler_type: "bridge", destructive: false }],
  ["project-tool.summary", { handler_type: "bridge", destructive: false }],
  ["project-tool.describe", { handler_type: "bridge", destructive: false }],
  ["project-tool.list", { handler_type: "bridge", destructive: false }],
  ["project-tool.run", { handler_type: "bridge", destructive: false }],
  ["runtime-tool.groups", { handler_type: "bridge", destructive: false }],
  ["runtime-tool.summary", { handler_type: "bridge", destructive: false }],
  ["runtime-tool.describe", { handler_type: "bridge", destructive: false }],
  ["runtime-tool.list", { handler_type: "bridge", destructive: false }],
  ["runtime-tool.run", { handler_type: "bridge", destructive: false }]
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

for (const descriptor of staticCatalog.tools) {
  const name = String(descriptor.name);
  if (!publicToolNames.has(name) || entries.has(name)) continue;
  const group = String(descriptor.group);
  entries.set(name, {
    bridgeCommand: name,
    group,
    command: commandName(name, group),
    handler_type: "bridge",
    csharp_handler: "present",
    destructive: descriptor.destructiveHint === true,
    timeout: DEFAULT_TIMEOUT_MS,
    ...(Array.isArray(descriptor.requiresPackages) && descriptor.requiresPackages.length ? { requires_packages: [...descriptor.requiresPackages] } : {})
  });
}

for (const spec of bridgeCommandRegistry.specs()) {
  const key = spec.toolName ?? spec.bridgeCommand;
  if (entries.has(key)) continue;

  const override = spec.toolName ? undefined : bridgeOnlyEntries.get(spec.bridgeCommand);
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
  generatedFrom: "unified C#/TS/metadata command contract",
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

/** static descriptor 输入必须是唯一、完整的公开名称集合。 */
function descriptorNames(descriptors) {
  if (!Array.isArray(descriptors)) throw new Error("Static capability catalog tools must be an array.");
  const names = descriptors.map((descriptor, index) => {
    if (!descriptor || typeof descriptor !== "object" || Array.isArray(descriptor) || typeof descriptor.name !== "string" || descriptor.name.length === 0) {
      throw new Error(`Static capability catalog tools[${index}] is missing a valid name.`);
    }
    return descriptor.name;
  });
  if (new Set(names).size !== names.length) throw new Error("Static capability catalog contains duplicate Tool names.");
  return names;
}

/** 生成前闭合 source inventory 与 static snapshot，禁止静默忽略 stale descriptor。 */
function assertExactNameSet(sourceNames, staticNames) {
  const source = new Set(sourceNames);
  const snapshot = new Set(staticNames);
  const missing = [...source].filter((name) => !snapshot.has(name)).sort();
  const stale = [...snapshot].filter((name) => !source.has(name)).sort();
  if (missing.length === 0 && stale.length === 0) return;
  throw new Error(`Static capability catalog does not match canonical C# source inventory; missing=[${missing.join(", ")}], stale=[${stale.join(", ")}]. Regenerate the static catalog from the integrated canonical package before generating command-contract.json.`);
}
