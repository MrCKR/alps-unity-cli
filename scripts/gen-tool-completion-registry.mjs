import fs from "node:fs";
import path from "node:path";
import { collectPublicToolInventory } from "./lib/public-tool-inventory.mjs";
import { validateProBuilderCompletionScope } from "./lib/probuilder-completion-scope.mjs";

const root = process.cwd();
const contractPath = path.join(root, "contracts", "tool-completion-contract.json");
const contract = JSON.parse(fs.readFileSync(contractPath, "utf8"));
const inventory = collectPublicToolInventory(root);
const entries = Array.isArray(contract.tools) ? contract.tools : [];
const failures = [];

const inventoryByName = uniqueMap(inventory, (entry) => entry.name, "public Tool inventory");
const contractByName = uniqueMap(entries, (entry) => entry.toolName, "completion contract");
for (const name of inventoryByName.keys()) if (!contractByName.has(name)) failures.push(`completion contract missing ${name}`);
for (const name of contractByName.keys()) if (!inventoryByName.has(name)) failures.push(`orphan completion contract ${name}`);
failures.push(...validateProBuilderCompletionScope(entries));

const normalized = [];
for (const tool of inventory) {
  const completion = contractByName.get(tool.name);
  if (!completion) continue;
  normalized.push(normalize(tool, completion));
}
const csharpEntries = normalized.filter((entry) => entry.descriptorOwner === "built-in" || entry.descriptorOwner.startsWith("extension:"));
const editorCompile = normalized.find((entry) => entry.toolName === "editor.compile" && entry.descriptorOwner === "ts-local");
const codeExec = normalized.find((entry) => entry.toolName === "debug.code.execute" && entry.descriptorOwner === "ts-local");
const buildRun = normalized.find((entry) => entry.toolName === "build.run" && entry.descriptorOwner === "ts-local");
const editorRefresh = normalized.find((entry) => entry.toolName === "editor.refresh" && entry.descriptorOwner === "ts-local");
const testRun = normalized.find((entry) => entry.toolName === "test.run" && entry.descriptorOwner === "ts-local");
const packageOperations = ["resolve", "install", "remove", "deploy", "restore"];
const packageRoots = packageOperations.map((operation) => normalized.find((entry) => entry.toolName === `package.${operation}` && entry.descriptorOwner === "ts-local"));
if (!editorCompile) failures.push("TS-owned editor.compile completion contract is required by the private C# coordinator");
if (!codeExec) failures.push("TS-owned debug.code.execute completion contract is required by the C# coordinator");
if (!buildRun) failures.push("TS-owned build.run completion contract is required by the private C# coordinator");
if (!editorRefresh) failures.push("TS-owned editor.refresh completion contract is required by the private C# coordinator");
if (!testRun) failures.push("TS-owned test.run completion contract is required by the private C# coordinator");
for (let index = 0; index < packageRoots.length; index += 1)
  if (!packageRoots[index]) failures.push(`TS-owned package.${packageOperations[index]} completion contract is required by the private C# coordinator`);
if (failures.length > 0) throw new Error(failures.join("\n"));

const tsOutput = path.join(root, "src", "tools", ".generated-completion-registry.ts");
const csharpOutput = path.join(root, "unitypkg", "Editor", "Generated", "UnityCliToolCompletionRegistry.g.cs");
fs.mkdirSync(path.dirname(tsOutput), { recursive: true });
fs.mkdirSync(path.dirname(csharpOutput), { recursive: true });
fs.writeFileSync(tsOutput, renderTypeScript(normalized), "utf8");
fs.writeFileSync(csharpOutput, renderCSharp(csharpEntries, editorCompile, codeExec, buildRun, editorRefresh, testRun, packageOperations, packageRoots), "utf8");
process.stdout.write(`generated ${path.relative(root, tsOutput)} (${normalized.length} completion contracts)\n`);
process.stdout.write(`generated ${path.relative(root, csharpOutput)} (${csharpEntries.length} C# completion contracts)\n`);

/** 将人读契约收敛成运行时只能枚举分支的机器字段。 */
function normalize(tool, completion) {
  const executionClass = requiredEnum(completion.executionClass, ["atomic", "observed-state", "resumable", "external"], `${tool.name}.executionClass`);
  const deliveryMode = "direct-terminal";
  const completionPredicate = requiredKind(completion.completionPredicate, `${tool.name}.completionPredicate`);
  const proofSource = requiredKind(completion.proofSource, `${tool.name}.proofSource`);
  const reloadPolicy = requiredKind(completion.reloadPolicy, `${tool.name}.reloadPolicy`);
  const outcomeUnknownPolicy = requiredKind(completion.outcomeUnknownPolicy, `${tool.name}.outcomeUnknownPolicy`);
  const timeoutError = requiredString(completion.timeoutError, `${tool.name}.timeoutError`);
  const mutation = isMutation(sourceMutationDescriptor(tool));
  return {
    toolName: tool.name,
    descriptorOwner: tool.descriptorOwner,
    deliveryMode,
    executionClass,
    completionPredicate,
    proofSource,
    reloadPolicy,
    timeoutError,
    outcomeUnknownPolicy,
    completionDriverId: driverId(executionClass),
    assemblyName: tool.assemblyName,
    implementationType: tool.implementationType,
    journalPolicy: mutation === true ? "required" : "none",
    mutation: mutation === true,
  };
}

function sourceMutationDescriptor(tool) {
  const source = fs.readFileSync(path.join(root, tool.source), "utf8");
  if (tool.descriptorOwner === "ts-local" && !/\bdefineTool(?:<[^>]+>)?\s*\(/.test(source))
    throw new Error(`${tool.source}: defineTool declaration is missing`);
  if (tool.descriptorOwner !== "ts-local" && !/\bIUnityCliTool\s*</.test(source))
    throw new Error(`${tool.source}: IUnityCliTool declaration is missing`);
  const csharp = tool.descriptorOwner !== "ts-local";
  return {
    destructiveHint: csharp ? /\bDestructiveHint\s*=\s*true\b/.test(source) : /\bdestructiveHint:\s*true\b/.test(source),
    supportsDryRun: csharp ? /\bSupportsDryRun\s*=\s*true\b/.test(source) : /\bsupportsDryRun:\s*true\b/.test(source),
    sideEffects: (csharp ? /\bSideEffects\s*=\s*new\[\]\s*\{\s*[^}\s]/.test(source) : /\bsideEffects:\s*\[\s*[^\]\s]/.test(source)) ? ["declared"] : [],
    playModeSupport: (csharp ? /\bPlayModeSupport\s*=\s*"session-affecting"/.test(source) : /\bplayModeSupport:\s*["']session-affecting["']/.test(source)) ? "session-affecting" : undefined,
  };
}

function isMutation(descriptor) {
  return descriptor.destructiveHint === true
    || descriptor.supportsDryRun === true
    || (Array.isArray(descriptor.sideEffects) && descriptor.sideEffects.length > 0)
    || descriptor.playModeSupport === "session-affecting";
}

function driverId(executionClass) {
  if (executionClass === "atomic") return "atomic-typed-result-v1";
  if (executionClass === "observed-state") return "observed-terminal-readback-v1";
  if (executionClass === "resumable") return "active-operation-terminal-readback-v1";
  return "external-terminal-readback-v1";
}

function renderTypeScript(items) {
  return `// <auto-generated />\n// 由 scripts/gen-tool-completion-registry.mjs 从唯一 completion contract 生成。\n\nexport interface GeneratedToolCompletionContract {\n  toolName: string;\n  descriptorOwner: string;\n  deliveryMode: \"direct-terminal\";\n  executionClass: \"atomic\" | \"observed-state\" | \"resumable\" | \"external\";\n  completionPredicate: string;\n  proofSource: string;\n  reloadPolicy: string;\n  timeoutError: string;\n  outcomeUnknownPolicy: string;\n  completionDriverId: string;\n  assemblyName?: string;\n  implementationType?: string;\n  journalPolicy: \"required\" | \"none\";\n  mutation: boolean;\n}\n\nexport const GENERATED_TOOL_COMPLETION_CONTRACTS: readonly GeneratedToolCompletionContract[] = ${JSON.stringify(items, null, 2)};\n\nconst BY_NAME = new Map(GENERATED_TOOL_COMPLETION_CONTRACTS.map((entry) => [entry.toolName, entry]));\n\n/** public Tool 执行前必须命中唯一 completion contract。 */\nexport function generatedToolCompletionContract(name: string): GeneratedToolCompletionContract | undefined {\n  return BY_NAME.get(name);\n}\n`;
}

function renderCSharp(items, editorCompile, codeExec, buildRun, editorRefresh, testRun, packageOperations, packageRoots) {
  const contractExpression = (entry) => `new UnityCliToolCompletionContract(${cs(entry.toolName)}, ${cs(entry.descriptorOwner)}, ${cs(entry.deliveryMode)}, ${cs(entry.executionClass)}, ${cs(entry.completionPredicate)}, ${cs(entry.proofSource)}, ${cs(entry.reloadPolicy)}, ${cs(entry.timeoutError)}, ${cs(entry.outcomeUnknownPolicy)}, ${cs(entry.completionDriverId)}, ${cs(entry.journalPolicy)}, ${entry.mutation ? "true" : "false"})`;
  const rows = items.map((entry) => `            Add(${contractExpression(entry)});`).join("\n");
  const inventoryRows = items.map((entry) => `            AddInventory(new UnityCliToolInventoryEntry(${cs(entry.toolName)}, ${cs(entry.descriptorOwner)}, ${cs(entry.assemblyName ?? "")}, ${cs(entry.implementationType ?? "")}));`).join("\n");
  const editorCompileExpression = contractExpression(editorCompile);
  const codeExecExpression = contractExpression(codeExec);
  const buildRunExpression = contractExpression(buildRun);
  const editorRefreshExpression = contractExpression(editorRefresh);
  const testRunExpression = contractExpression(testRun);
  const packageRootRows = packageOperations.map((operation, index) => `            { ${cs(operation)}, ${contractExpression(packageRoots[index])} }`).join(",\n");
  return `// <auto-generated />\n#if UNITY_EDITOR\nusing System;\nusing System.Collections.Generic;\n\nnamespace UnityCli\n{\n    internal sealed class UnityCliToolInventoryEntry\n    {\n        public UnityCliToolInventoryEntry(string toolName, string descriptorOwner, string assemblyName, string implementationType)\n        {\n            ToolName = toolName; DescriptorOwner = descriptorOwner; AssemblyName = assemblyName; ImplementationType = implementationType;\n        }\n        public string ToolName { get; }\n        public string DescriptorOwner { get; }\n        public string AssemblyName { get; }\n        public string ImplementationType { get; }\n    }\n\n    internal sealed class UnityCliToolCompletionContract\n    {\n        public UnityCliToolCompletionContract(string toolName, string descriptorOwner, string deliveryMode, string executionClass, string completionPredicate, string proofSource, string reloadPolicy, string timeoutError, string outcomeUnknownPolicy, string completionDriverId, string journalPolicy, bool mutation)\n        {\n            ToolName = toolName; DescriptorOwner = descriptorOwner; DeliveryMode = deliveryMode; ExecutionClass = executionClass;\n            CompletionPredicate = completionPredicate; ProofSource = proofSource; ReloadPolicy = reloadPolicy; TimeoutError = timeoutError;\n            OutcomeUnknownPolicy = outcomeUnknownPolicy; CompletionDriverId = completionDriverId; JournalPolicy = journalPolicy; Mutation = mutation;\n        }\n        public string ToolName { get; private set; }\n        public string DescriptorOwner { get; private set; }\n        public string DeliveryMode { get; private set; }\n        public string ExecutionClass { get; private set; }\n        public string CompletionPredicate { get; private set; }\n        public string ProofSource { get; private set; }\n        public string ReloadPolicy { get; private set; }\n        public string TimeoutError { get; private set; }\n        public string OutcomeUnknownPolicy { get; private set; }\n        public string CompletionDriverId { get; private set; }\n        public string JournalPolicy { get; private set; }\n        public bool Mutation { get; private set; }\n    }\n\n    internal static class UnityCliToolCompletionRegistry\n    {\n        private static readonly Dictionary<string, UnityCliToolCompletionContract> Contracts = Create();\n        private static readonly Dictionary<string, UnityCliToolInventoryEntry> Inventory = CreateInventory();\n        internal static readonly UnityCliToolCompletionContract EditorCompile = ${editorCompileExpression};\n        internal static readonly UnityCliToolCompletionContract BuildRun = ${buildRunExpression};\n        internal static readonly UnityCliToolCompletionContract EditorRefresh = ${editorRefreshExpression};\n        internal static readonly UnityCliToolCompletionContract CodeExec = ${codeExecExpression};\n        internal static readonly UnityCliToolCompletionContract TestRun = ${testRunExpression};\n        private static readonly Dictionary<string, UnityCliToolCompletionContract> PackageRoots = new Dictionary<string, UnityCliToolCompletionContract>(StringComparer.Ordinal)\n        {\n${packageRootRows}\n        };\n        public static bool TryGet(string toolName, out UnityCliToolCompletionContract contract) => Contracts.TryGetValue(toolName ?? string.Empty, out contract);\n        internal static bool TryGetInventory(string toolName, out UnityCliToolInventoryEntry entry) => Inventory.TryGetValue(toolName ?? string.Empty, out entry);\n        internal static int InventoryCount => Inventory.Count;\n        internal static IEnumerable<UnityCliToolInventoryEntry> InventoryEntries() => Inventory.Values;\n\n        private static Dictionary<string, UnityCliToolInventoryEntry> CreateInventory()\n        {\n            var result = new Dictionary<string, UnityCliToolInventoryEntry>(StringComparer.Ordinal);\n            void AddInventory(UnityCliToolInventoryEntry entry)\n            {\n                if (result.ContainsKey(entry.ToolName)) throw new InvalidOperationException(\"Duplicate generated Tool inventory: \" + entry.ToolName);\n                result.Add(entry.ToolName, entry);\n            }\n${inventoryRows}\n            return result;\n        }\n        internal static bool TryGetPackageRoot(string operation, out UnityCliToolCompletionContract contract) => PackageRoots.TryGetValue(operation ?? string.Empty, out contract);\n        public static int Count => Contracts.Count;\n\n        private static Dictionary<string, UnityCliToolCompletionContract> Create()\n        {\n            var result = new Dictionary<string, UnityCliToolCompletionContract>(StringComparer.Ordinal);\n            void Add(UnityCliToolCompletionContract contract)\n            {\n                if (result.ContainsKey(contract.ToolName)) throw new InvalidOperationException(\"Duplicate generated completion contract: \" + contract.ToolName);\n                result.Add(contract.ToolName, contract);\n            }\n${rows}\n            return result;\n        }\n    }\n}\n#endif\n`;
}

function uniqueMap(items, keyOf, label) {
  const result = new Map();
  for (const item of items) {
    const key = keyOf(item);
    if (typeof key !== "string" || key.length === 0) throw new Error(`${label} contains an invalid key`);
    if (result.has(key)) throw new Error(`${label} contains duplicate ${key}`);
    result.set(key, item);
  }
  return result;
}

function requiredKind(value, location) {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error(`${location} must be an object`);
  return requiredString(value.kind, `${location}.kind`);
}
function requiredString(value, location) {
  if (typeof value !== "string" || value.length === 0) throw new Error(`${location} must be a non-empty string`);
  return value;
}
function optionalKind(value, fallback, allowed, location) {
  if (value === undefined) return fallback;
  const kind = requiredKind(value, location);
  if (!allowed.includes(kind)) throw new Error(`${location}.kind has unsupported value ${kind}`);
  return kind;
}
function requiredEnum(value, allowed, location) {
  const text = requiredString(value, location);
  if (!allowed.includes(text)) throw new Error(`${location} has unsupported value ${text}`);
  return text;
}
function cs(value) {
  const escaped = String(value)
    .replaceAll("\\", "\\\\")
    .replaceAll('"', '\\"')
    .replaceAll("\r", "\\r")
    .replaceAll("\n", "\\n");
  return `\"${escaped}\"`;
}
