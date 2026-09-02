import assert from "node:assert/strict";
import { access, readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const toolInterfacePath = "unitypkg/Editor/ProjectTools/IUnityCliTool.cs";
const toolRegistryPath = "unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs";
const registeredToolPath = "unitypkg/Editor/ProjectTools/RegisteredTool.cs";
const runtimeExecutorPath = "unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs";
const bridgePath = "unitypkg/Editor/UnityCliBridge.cs";
const ownershipPath = "unitypkg/Editor/Infrastructure/RegistryOwnershipResolver.cs";
const runtimeBridgeDomainPath = "src/bridge/domains/runtime-tool.ts";
const toolDispatcherPath = "src/tools/dispatcher.ts";

async function readSources(paths: string[]): Promise<string> {
  return (await Promise.all(paths.map((path) => readFile(path, "utf8")))).join("\n");
}

async function sourceFiles(root: string): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "Generated") continue;
      result.push(...await sourceFiles(path));
    } else if (/\.(?:cs|ts|md)$/.test(entry.name) && !entry.name.startsWith(".generated")) result.push(path);
  }
  return result;
}

test("typed Tool input cannot become selector or execution flags", async () => {
  const dispatcher = await readFile(toolDispatcherPath, "utf8");
  assert.match(dispatcher, /const dispatchFlags = withStructuredInput\(flags, parsed\.input\)/);
  assert.match(dispatcher, /TOOL_CONTROL_FLAGS[\s\S]*"instance"[\s\S]*"runtime"[\s\S]*"timeout-ms"/);
  assert.doesNotMatch(dispatcher, /function mergeInputFlags/);
  assert.doesNotMatch(dispatcher, /Object\.entries\(input\)[\s\S]*merged\[flag\]/);
});

test("InitializeOnLoad constructor is the only domain catalog construction trigger", async () => {
  const [bridge, registry, runtime] = await Promise.all([
    readFile(bridgePath, "utf8"),
    readFile(toolRegistryPath, "utf8"),
    readFile(runtimeExecutorPath, "utf8"),
  ]);
  const constructor = bridge.slice(bridge.indexOf("static UnityCliBridge()"), bridge.indexOf("private static void ScheduleBridgeStartup"));
  assert.match(constructor, /if \(AssetDatabase\.IsAssetImportWorkerProcess\(\)\) return;[\s\S]*InitializeProjectToolCatalog\(\)[\s\S]*ScheduleBridgeStartup\(\)/);
  assert.equal((bridge.match(/InitializeProjectToolCatalog\(\)/g) ?? []).length, 1);
  assert.equal((registry.match(/private static void InitializeProjectToolCatalog\(\)/g) ?? []).length, 1);
  assert.doesNotMatch(runtime, /AppDomain\.CurrentDomain\.GetAssemblies|assembly\.GetTypes\(\)/);
});

test("catalog construction validates a local pending table and atomically publishes one read-only snapshot", async () => {
  const [registry, registered, ownership] = await Promise.all([
    readFile(toolRegistryPath, "utf8"),
    readFile(registeredToolPath, "utf8"),
    readFile(ownershipPath, "utf8"),
  ]);
  assert.match(registry, /var builder = new UnityCliToolCatalogBuilder\(\)/);
  assert.match(registry, /builder\.Publish[\s\S]*Volatile\.Write\(ref publishedProjectToolCatalog, snapshot\)/);
  assert.match(registry, /ReadPublishedProjectToolCatalog\(\)[\s\S]*Volatile\.Read/);
  assert.match(registered, /IUnityCliToolCatalogSnapshotReader[\s\S]*UnityCliToolCatalogSnapshot Read\(\)/);
  assert.match(registered, /ReadOnlyDictionary<string, RegisteredTool>/);
  assert.match(registered, /faulted = true[\s\S]*cannot publish an incomplete or faulted table/);
  assert.match(registered, /Duplicate tool registration/);
  assert.match(registered, /valid object schemas/);
  assert.match(registered, /complete completion contract/);
  assert.match(ownership, /only uses compiled|已编译assembly identity/);
  assert.doesNotMatch(ownership, /File\.|Directory\.|AssetDatabase/);
});

test("later list, run, completion, Runtime, and preflight access only the published snapshot", async () => {
  const sources = await readSources([
    toolRegistryPath,
    runtimeExecutorPath,
    "unitypkg/Editor/Tools/diagnose/preflight.cs",
  ]);
  assert.match(sources, /ProjectToolCatalogSnapshotReader\.Read\(\)/);
  assert.match(sources, /ReadPublishedProjectToolCatalog\(\)/);
  assert.doesNotMatch(sources, /AssetDatabase\.FindAssets|File\.ReadAllText|File\.WriteAllText/);
  assert.equal(sources.includes(["custom", "tool", "registered"].join("_")), false);
  assert.equal((sources.match(/tool_catalog_initialized assemblies=/g) ?? []).length, 1);
  assert.match(sources, /foreach \(var pair in catalog\.Tools\)[\s\S]*\{ "available", playModeReady \}/);
});

test("typed custom Tools retain async schema, completion, availability, and execution contracts", async () => {
  const [toolInterface, registry] = await Promise.all([
    readFile(toolInterfacePath, "utf8"),
    readFile(toolRegistryPath, "utf8"),
  ]);
  assert.match(toolInterface, /ValueTask<TRes>\s+ExecuteAsync\(TReq request, UnityCliProjectToolContext ctx, CancellationToken ct/);
  assert.match(toolInterface, /ToolCompletionDescriptor\s+Completion/);
  assert.doesNotMatch(toolInterface, /IUnityCliContext|ContextDescriptor|ContextExample/);
  assert.match(registry, /custom\.CompletionDriverId, mutation \? "required" : "none", mutation/);
  assert.match(registry, /\{ "completionContract", CompletionContractData\(definition\.CompletionContract\) \}/);
  assert.match(registry, /\{ "available", ToolAvailableNow\(definition\) \}/);
});

test("legacy project Context registries and authoring docs are absent", async () => {
  for (const path of [
    "unitypkg/Editor/ProjectContexts",
    "unitypkg/Editor/Contexts",
    "src/contexts",
    "src/bridge/domains/context.ts",
    "skills/unitycli/references/custom-contexts.md",
  ]) await assert.rejects(access(path), { code: "ENOENT" });
});

test("non-generated C#, TypeScript, docs, and tests contain no removed per-Tool identity or source-verification path", async () => {
  const forbidden = [
    ["Project", "Tool", "Handler", "Source"].join(""),
    ["internal", "Descriptor", "Fingerprint"].join(""),
    ["live", "Descriptor", "Fingerprint"].join(""),
    ["expected", "Descriptor", "Fingerprint"].join(""),
    ["Expected", "Descriptor", "Fingerprint"].join(""),
    ["E", "_TOOL", "_DESCRIPTOR", "_CHANGED"].join(""),
    ["E", "_TOOL", "_FINGERPRINT", "_MISMATCH"].join(""),
    ["ToolDescriptor", ".", "Fingerprint"].join(""),
    ["custom", "_tool", "_registered"].join(""),
  ];
  const files = (await Promise.all(["unitypkg/Editor", "unitypkg/Tests", "src", "docs", "tests"].map(sourceFiles))).flat();
  const residuals: string[] = [];
  for (const path of files) {
    const source = await readFile(path, "utf8");
    for (const token of forbidden) if (source.includes(token)) residuals.push(`${path}: ${token}`);
  }
  assert.deepEqual(residuals, []);
});

test("published custom Tool example matches the current async API", async () => {
  const toolDocs = await readFile("skills/unitycli/references/custom-tools.md", "utf8");
  assert.match(toolDocs, /ValueTask<Result> ExecuteAsync\(/);
  assert.match(toolDocs, /Completion = new ToolCompletionDescriptor/);
  assert.doesNotMatch(toolDocs, /public Result Execute\(/);
});
