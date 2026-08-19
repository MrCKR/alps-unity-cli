import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { executeBridgeCommand } from "../dist/src/bridge/index.js";
import { createRequestId } from "../dist/src/output.js";
import { defaultDataDir } from "../dist/src/platform.js";
import { projectDescriptorToToolMetadata } from "../dist/src/project-tool-discovery.js";
import { GENERATED_TOOL_COMPLETION_CONTRACTS } from "../dist/src/tools/.generated-completion-registry.js";
import { collectPublicToolInventory } from "./lib/public-tool-inventory.mjs";
import { extractConditionalToolDescriptor } from "./lib/conditional-csharp-descriptor.mjs";

const options = parseArgs(process.argv.slice(2));
if (!options.instance) throw new Error("Pass --instance <canonicalProjectPath|instanceId>.");
const output = resolve(options.output ?? "contracts/static-capability-catalog.json");
const flags = { instance: options.instance, "timeout-ms": "120000" };
const runtime = { dataDir: defaultDataDir() };

const toolsResult = await executeBridgeCommand(
  createRequestId(),
  "project-tool.list",
  flags,
  undefined,
  runtime,
  undefined,
  { mode: "read", mutation: false, defaultTimeoutMs: 120_000 },
);
if (!toolsResult.ok) throw new Error(`${toolsResult.error.code}: ${toolsResult.error.message}`);

const completionContracts = new Map(GENERATED_TOOL_COMPLETION_CONTRACTS.map((contract) => [contract.toolName, contract]));
const tools = arrayField(toolsResult.data, "tools")
  .filter((item) => item.source === "built-in" || item.source === "extension")
  .map(withGeneratedCompletionContract)
  .map(toStaticToolDescriptor)
  .map(stripDynamicToolFields);
const expectedTools = collectPublicToolInventory(process.cwd())
  .filter((tool) => tool.descriptorOwner === "built-in" || tool.descriptorOwner.startsWith("extension:"));
const capturedNames = new Set(tools.map((tool) => tool.name));
for (const inventoryEntry of expectedTools) {
  if (!capturedNames.has(inventoryEntry.name)) {
    const completionContract = completionContracts.get(inventoryEntry.name);
    if (!completionContract) throw new Error(`Missing generated completion contract for ${inventoryEntry.name}.`);
    const descriptor = { ...extractConditionalToolDescriptor(process.cwd(), inventoryEntry), completionContract, available: true };
    tools.push(stripDynamicToolFields(toStaticToolDescriptor(descriptor)));
  }
}
tools.sort(byName);
assertUnique(tools, "Tool");

const catalog = {
  version: 1,
  semantics: "Offline built-in and Extension metadata captured from one successfully compiled Live Editor package; project-defined capabilities are never packaged here.",
  tools,
};
await mkdir(dirname(output), { recursive: true });
await writeFile(output, `${JSON.stringify(catalog, null, 2)}\n`, "utf8");
process.stdout.write(`${JSON.stringify({ ok: true, output, tools: tools.length })}\n`);

/** 解析生成器参数；该脚本只在 Dev Bridge 已通过 strict identity 后运行。 */
function parseArgs(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index += 1) {
    if (argv[index] === "--instance") result.instance = argv[++index];
    else if (argv[index] === "--output") result.output = argv[++index];
    else throw new Error(`Unknown argument: ${argv[index]}`);
  }
  return result;
}

/** 提取 Bridge response 中的 descriptor 数组并拒绝 malformed payload。 */
function arrayField(data, field) {
  if (!data || typeof data !== "object" || !Array.isArray(data[field])) {
    throw new Error(`Bridge response is missing ${field} array.`);
  }
  return data[field].map((item) => {
    if (!item || typeof item !== "object" || Array.isArray(item) || typeof item.name !== "string") {
      throw new Error(`${field} contains a malformed descriptor.`);
    }
    return item;
  });
}

/** Live raw descriptor 与 tools describe 一样先绑定 generated completion contract；缺失表示 catalog 不完整。 */
function withGeneratedCompletionContract(descriptor) {
  const completionContract = completionContracts.get(descriptor.name);
  if (!completionContract) throw new Error(`Missing generated completion contract for ${descriptor.name}.`);
  return { ...descriptor, completionContract };
}

/** static catalog 保存完整内部契约，不能复用面向 AI 的精简 describe 投影。 */
function toStaticToolDescriptor(descriptor) {
  const metadata = projectDescriptorToToolMetadata(descriptor);
  if (!metadata) throw new Error(`Malformed Live Tool descriptor: ${String(descriptor.name ?? "unknown")}`);
  return {
    ...metadata,
    examples: metadata.examples?.map(({ description, argv }) => ({ description, argv })),
    source: descriptor.source,
  };
}

/** session 可用性由调用时重新计算，不能冻结进 package snapshot。 */
function stripDynamicToolFields(tool) {
  const stable = pickFields(tool, [
    "name", "summary", "description", "group", "inputSchema", "outputSchema",
    "destructiveHint", "sideEffects", "playModeSupport", "supportsDryRun", "contractProfile", "permissions",
    "examples", "source", "target", "availabilityScope", "requiresPackages", "extensionId"
  ]);
  return omitEmptyOptionalArrays(stable, new Set(["permissions", "requiresPackages", "sideEffects"]));
}

/** snapshot 使用内部稳定字段 allowlist，运行时路由和完成字段默认不落盘。 */
function pickFields(descriptor, allowedFields) {
  return Object.fromEntries(allowedFields.filter((key) => Object.hasOwn(descriptor, key)).map((key) => [key, descriptor[key]]));
}

/** static snapshot 不写可选空数组，避免无意义的 package 体积。 */
function omitEmptyOptionalArrays(descriptor, optionalFields) {
  return Object.fromEntries(Object.entries(descriptor).filter(([key, value]) => !(optionalFields.has(key) && Array.isArray(value) && value.length === 0)));
}

function assertUnique(items, kind) {
  const seen = new Set();
  for (const item of items) {
    if (seen.has(item.name)) throw new Error(`Duplicate static ${kind}: ${item.name}`);
    seen.add(item.name);
  }
}

function byName(left, right) {
  return left.name.localeCompare(right.name);
}
