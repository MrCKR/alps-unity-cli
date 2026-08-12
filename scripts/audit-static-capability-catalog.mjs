import fs from "node:fs";
import path from "node:path";
import { collectPublicToolInventory } from "./lib/public-tool-inventory.mjs";

const root = process.cwd();
const catalogPath = path.join(root, "contracts", "static-capability-catalog.json");
const catalog = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
const failures = [];

const expectedTools = collectPublicToolInventory(root)
  .filter((tool) => tool.descriptorOwner === "built-in" || tool.descriptorOwner.startsWith("extension:"))
  .map((tool) => tool.name)
  .sort();
const actualTools = names(catalog.tools, "Tool");
compareSets("Tool", expectedTools, actualTools);
if (Object.hasOwn(catalog, "contexts")) failures.push("Static catalog contains forbidden legacy contexts field");

const forbiddenDescriptorFields = new Set([
  "artifactKinds", "capabilityType", "cliCommand", "completionContract", "defaultTimeoutMs", "descriptorFingerprint",
  "fingerprint", "handler", "internalDescriptorFingerprint", "maxTimeoutMs", "refreshBehavior", "supportStatus", "supportsJob", "version",
]);
const forbiddenOutputFields = new Set([
  "actualPort", "audit", "bridgeCommand", "bridgeEndpoint", "bridgeEndpointUrl", "bridgeUrl", "command", "commandLine",
  "descriptorFingerprint", "endpoint", "endpointUrl", "execution", "handler", "handlerFingerprint", "hwnd",
  "internalDescriptorFingerprint", "listenerGeneration", "listenerId", "logPath", "logs", "ownerHwnd", "port", "preferredPort",
  "route", "routing", "sourceListenerGeneration", "windowsCommandLine",
]);
for (const tool of catalog.tools ?? []) {
  validateDescriptor(tool, "Tool");
  validatePublicDescriptorFields(tool, "Tool", forbiddenDescriptorFields);
  failures.push(...findSchemaProperties(tool.outputSchema, `Tool ${tool.name}.outputSchema`, forbiddenOutputFields));
}
const result = {
  ok: failures.length === 0,
  catalog: path.relative(root, catalogPath).replaceAll("\\", "/"),
  toolCount: actualTools.length,
  failures,
};
process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
if (!result.ok) process.exitCode = 1;

/** Descriptor snapshot 必须足以完成内部契约冻结审计，不进入公开 discovery。 */
function validateDescriptor(descriptor, kind) {
  for (const field of ["name", "summary", "description", "source", "target", "availabilityScope", "playModeSupport", "inputSchema", "outputSchema", "examples"]) {
    if (descriptor?.[field] === undefined || descriptor?.[field] === "") failures.push(`${kind} ${descriptor?.name ?? "unknown"} is missing ${field}`);
  }
  if (!Array.isArray(descriptor?.examples) || descriptor.examples.length === 0) failures.push(`${kind} ${descriptor?.name ?? "unknown"} has no examples`);
  else for (const [index, example] of descriptor.examples.entries()) {
    const keys = Object.keys(example ?? {}).sort();
    if (keys.join(",") !== "argv,description" || !Array.isArray(example?.argv) || example.argv.some((token) => typeof token !== "string")) {
      failures.push(`${kind} ${descriptor?.name ?? "unknown"} examples[${index}] is not a public argv-only example`);
    }
  }
  if (descriptor?.source !== "built-in" && descriptor?.source !== "extension") failures.push(`${kind} ${descriptor?.name ?? "unknown"} has non-static source`);
  for (const field of ["permissions", "requiresPackages", "sideEffects"]) {
    if (Object.hasOwn(descriptor ?? {}, field) && (!Array.isArray(descriptor[field]) || descriptor[field].length === 0)) failures.push(`${kind} ${descriptor?.name ?? "unknown"} must omit empty ${field}`);
  }
}

/** descriptor 顶层只允许公开契约字段；内部执行字段无论值为何都禁止冻结。 */
function validatePublicDescriptorFields(descriptor, kind, forbiddenFields) {
  for (const field of forbiddenFields) if (Object.hasOwn(descriptor ?? {}, field)) failures.push(`${kind} ${descriptor?.name ?? "unknown"} exposes ${field}`);
}

function names(entries, kind) {
  if (!Array.isArray(entries)) {
    failures.push(`${kind} catalog is not an array`);
    return [];
  }
  const result = entries.map((entry) => String(entry?.name ?? "")).sort();
  if (new Set(result).size !== result.length) failures.push(`${kind} catalog contains duplicate names`);
  return result;
}

function compareSets(kind, expected, actual) {
  const expectedSet = new Set(expected);
  const actualSet = new Set(actual);
  for (const name of expected) if (!actualSet.has(name)) failures.push(`${kind} missing from static catalog: ${name}`);
  for (const name of actual) if (!expectedSet.has(name)) failures.push(`Unexpected static ${kind}: ${name}`);
}

/** 只审计 JSON Schema 的 property names，避免把 description 中的业务术语误判为字段泄漏。 */
function findSchemaProperties(value, location, forbiddenFields) {
  if (!value || typeof value !== "object") return [];
  const findings = [];
  if (Array.isArray(value)) {
    value.forEach((item, index) => findings.push(...findSchemaProperties(item, `${location}[${index}]`, forbiddenFields)));
    return findings;
  }
  for (const [key, child] of Object.entries(value)) {
    const next = `${location}.${key}`;
    if (key === "properties" && child && typeof child === "object" && !Array.isArray(child)) {
      for (const property of Object.keys(child)) if (forbiddenFields.has(property)) findings.push(`${next}.${property} exposes a private field`);
    }
    findings.push(...findSchemaProperties(child, next, forbiddenFields));
  }
  return findings;
}
