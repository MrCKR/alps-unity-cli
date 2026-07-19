import fs from "node:fs";
import path from "node:path";
import { collectPublicContextInventory, collectPublicToolInventory } from "./lib/public-tool-inventory.mjs";

const root = process.cwd();
const catalogPath = path.join(root, "contracts", "static-capability-catalog.json");
const catalog = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
const failures = [];

const expectedTools = collectPublicToolInventory(root)
  .filter((tool) => tool.descriptorOwner === "built-in" || tool.descriptorOwner.startsWith("extension:"))
  .map((tool) => tool.name)
  .sort();
const expectedContexts = collectPublicContextInventory(root)
  .filter((context) => context.descriptorOwner === "built-in" || context.descriptorOwner.startsWith("extension:"))
  .map((context) => context.name)
  .sort();
const actualTools = names(catalog.tools, "Tool");
const actualContexts = names(catalog.contexts, "Context");
compareSets("Tool", expectedTools, actualTools);
compareSets("Context", expectedContexts, actualContexts);

for (const tool of catalog.tools ?? []) {
  validateDescriptor(tool, "Tool");
  if (!tool.outputSchema) failures.push(`Tool ${tool.name} is missing outputSchema`);
}
for (const context of catalog.contexts ?? []) {
  validateDescriptor(context, "Context");
  if (!context.outputSchema) failures.push(`Context ${context.name} is missing outputSchema`);
  if (!/^[a-f0-9]{64}$/.test(context.descriptorFingerprint ?? "")) failures.push(`Context ${context.name} is missing descriptorFingerprint`);
}

const forbidden = findForbidden(catalog, "$", new Set(["bridgeEndpoint", "listenerId", "actualPort", "sourceListenerGeneration"]));
failures.push(...forbidden.map((entry) => `Static catalog exposes private transport field ${entry}`));

const result = {
  ok: failures.length === 0,
  catalog: path.relative(root, catalogPath).replaceAll("\\", "/"),
  toolCount: actualTools.length,
  contextCount: actualContexts.length,
  failures,
};
process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
if (!result.ok) process.exitCode = 1;

/** Descriptor snapshot 必须足以让陌生 AI 离线发现和构造调用。 */
function validateDescriptor(descriptor, kind) {
  for (const field of ["name", "summary", "description", "source", "inputSchema", "examples"]) {
    if (descriptor?.[field] === undefined || descriptor?.[field] === "") failures.push(`${kind} ${descriptor?.name ?? "unknown"} is missing ${field}`);
  }
  if (!Array.isArray(descriptor?.examples) || descriptor.examples.length === 0) failures.push(`${kind} ${descriptor?.name ?? "unknown"} has no examples`);
  if (descriptor?.source !== "built-in" && descriptor?.source !== "extension") failures.push(`${kind} ${descriptor?.name ?? "unknown"} has non-static source`);
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

function findForbidden(value, location, forbiddenFields) {
  if (!value || typeof value !== "object") return [];
  const findings = [];
  if (Array.isArray(value)) {
    value.forEach((item, index) => findings.push(...findForbidden(item, `${location}[${index}]`, forbiddenFields)));
    return findings;
  }
  for (const [key, child] of Object.entries(value)) {
    const next = `${location}.${key}`;
    if (forbiddenFields.has(key)) findings.push(next);
    findings.push(...findForbidden(child, next, forbiddenFields));
  }
  return findings;
}
