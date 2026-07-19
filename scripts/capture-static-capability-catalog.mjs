import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { executeBridgeCommand, executeBridgePayload } from "../dist/bridge/index.js";
import { defaultDataDir } from "../dist/platform.js";
import { createRequestId } from "../dist/output.js";
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

const contextsResult = await executeBridgePayload(
  createRequestId(),
  "context.list",
  {},
  flags,
  runtime,
  undefined,
  { mode: "read", mutation: false, defaultTimeoutMs: 120_000 },
);
if (!contextsResult.ok) throw new Error(`${contextsResult.error.code}: ${contextsResult.error.message}`);

const tools = arrayField(toolsResult.data, "tools")
  .filter((item) => item.source === "built-in" || item.source === "extension")
  .map(stripDynamicToolFields);
const expectedTools = collectPublicToolInventory(process.cwd())
  .filter((tool) => tool.descriptorOwner === "built-in" || tool.descriptorOwner.startsWith("extension:"));
const capturedNames = new Set(tools.map((tool) => tool.name));
for (const inventoryEntry of expectedTools) {
  if (!capturedNames.has(inventoryEntry.name)) tools.push(extractConditionalToolDescriptor(process.cwd(), inventoryEntry));
}
tools.sort(byName);
const contexts = arrayField(contextsResult.data, "contexts")
  .filter((item) => item.source === "built-in" || item.source === "extension")
  .map(stripDynamicContextFields)
  .sort(byName);
assertUnique(tools, "Tool");
assertUnique(contexts, "Context");

const catalog = {
  version: 1,
  semantics: "Offline built-in and Extension metadata captured from one validated Unity listener generation; project-defined capabilities are never packaged here.",
  tools,
  contexts,
};
await mkdir(dirname(output), { recursive: true });
await writeFile(output, `${JSON.stringify(catalog, null, 2)}\n`, "utf8");
process.stdout.write(`${JSON.stringify({ ok: true, output, tools: tools.length, contexts: contexts.length })}\n`);

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

/** session 可用性由调用时重新计算，不能冻结进 package snapshot。 */
function stripDynamicToolFields(tool) {
  const { available, availabilityReason, ...stable } = tool;
  return stable;
}

/** Context snapshot 同样只保留稳定 descriptor 契约。 */
function stripDynamicContextFields(context) {
  const { available, availabilityReason, ...stable } = context;
  return stable;
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
