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
const runtimeBridgeDomainPath = "src/bridge/domains/catalog-player.ts";
const toolDispatcherPath = "src/tools/dispatcher.ts";

async function readSources(paths: string[]): Promise<string> {
  return (await Promise.all(paths.map((path) => readFile(path, "utf8")))).join("\n");
}

async function sourceFiles(root: string): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) {
      result.push(...await sourceFiles(path));
    } else if (/\.(?:cs|ts|md|json|mjs|js|cjs)$/.test(entry.name)) result.push(path);
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

test("catalog construction publishes one executable slot with an immutable descriptor-only reader", async () => {
  const [registry, registered, ownership] = await Promise.all([
    readFile(toolRegistryPath, "utf8"),
    readFile(registeredToolPath, "utf8"),
    readFile(ownershipPath, "utf8"),
  ]);
  assert.match(registry, /var builder = new UnityCliToolCatalogBuilder\(\)/);
  assert.match(registry, /var candidate = builder\.Publish\(elapsedMilliseconds, WriteProjectToolCatalogInitializedLog\)[\s\S]*Volatile\.Write\(ref publishedProjectToolCatalog, candidate\)/);
  assert.match(registry, /ReadPublishedProjectToolCatalogSnapshot\(\)[\s\S]*\.Snapshot/);
  assert.match(registered, /IUnityCliToolCatalogSnapshotReader[\s\S]*UnityCliToolCatalogSnapshot Read\(\)/);
  assert.match(registered, /ReadOnlyCollection<RegisteredToolDescriptor>/);
  assert.match(registered, /RegisteredToolDescriptor[\s\S]*CloneExamples\(\)/);
  assert.doesNotMatch(registered.slice(registered.indexOf("internal sealed class RegisteredToolDescriptor"), registered.indexOf("internal enum UnityCliToolProviderKind")), /InvokeAsync|RequestType|ResultType|OfficialAttributeBinding/);
  assert.doesNotMatch(registered.slice(registered.indexOf("internal sealed class RegisteredTool"), registered.indexOf("internal sealed class RegisteredToolDescriptor")), /\{ get; set; \}/);
  assert.match(registered, /faulted = true[\s\S]*cannot publish an incomplete or faulted table/);
  assert.match(registered, /providerMetadata\.Count != expectedProviderAssemblyCount/);
  assert.match(registered, /operations\.Record\(UnityCliToolCatalogOperationKind\.DiscoveryPass\)/);
  assert.match(registered, /operations\.Record\(UnityCliToolCatalogOperationKind\.ProviderMetadataRead\)/);
  assert.match(registered, /operations\.Record\(UnityCliToolCatalogOperationKind\.AggregateSuccessLog\)/);
  assert.equal((registered.match(/operations\.Record\(/g) ?? []).length, 3);
  assert.match(registered, /writeAggregateSuccessLog\(snapshot\)[\s\S]*published = true/);
  assert.match(ownership, /TryGetInventory\(toolName, out expected\)/);
  assert.match(ownership, /expected\.ImplementationType, implementationType\.FullName/);
  assert.match(ownership, /expected\/actual set mismatch/);
  assert.doesNotMatch(ownership, /StartsWith\(ExtensionAssemblyPrefix|File\.|Directory\.|AssetDatabase/);
});

test("later descriptor reads and executable runs remain on the same publication", async () => {
  const sources = await readSources([
    toolRegistryPath,
    runtimeExecutorPath,
  ]);
  assert.match(sources, /ProjectToolCatalogSnapshotReader\.Read\(\)/);
  assert.match(sources, /ReadPublishedProjectToolCatalogSnapshot\(\)/);
  assert.match(sources, /ReadPublishedProjectToolCatalog\(\)\.ExecutableTools/);
  assert.doesNotMatch(sources, /AssetDatabase\.FindAssets|File\.ReadAllText|File\.WriteAllText/);
  assert.equal(sources.includes(["custom", "tool", "registered"].join("_")), false);
  assert.equal((sources.match(/tool_catalog_initialized assemblies=/g) ?? []).length, 1);
  assert.match(sources, /foreach \(var descriptor in catalog\.Descriptors\)[\s\S]*\{ "available", playModeReady \}/);
});

test("unified generation emits exact C# owner inventory before runtime publication", async () => {
  const extensionIds = ["cinemachine", "hdrp", "probuilder", "urp", "vfx-graph"];
  const [generator, inventory, ownership, ...markers] = await Promise.all([
    readFile("scripts/gen-tool-completion-registry.mjs", "utf8"),
    readFile("scripts/lib/public-tool-inventory.mjs", "utf8"),
    readFile(ownershipPath, "utf8"),
    ...extensionIds.map((id) => readFile(`extensions/${id}/unitypkg/Editor/UnityCliExtensionAssembly.cs`, "utf8")),
  ]);
  assert.match(inventory, /UnityCliExtensionAssembly/);
  assert.match(inventory, /descriptorOwner/);
  assert.match(inventory, /assemblyName/);
  assert.match(inventory, /implementationType/);
  assert.match(generator, /UnityCliToolInventoryEntry/);
  assert.match(generator, /TryGetInventory/);
  assert.match(generator, /InventoryEntries/);
  assert.match(ownership, /GetCustomAttributes\(typeof\(UnityCliExtensionAssemblyAttribute\)/);
  assert.match(ownership, /expected\/actual set mismatch/);
  assert.match(ownership, /loaded without its exact UnityCliExtensionAssembly marker/);
  assert.doesNotMatch(ownership, /StartsWith\(ExtensionAssemblyPrefix/);
  for (let index = 0; index < extensionIds.length; index += 1) {
    assert.match(markers[index], new RegExp(`\\[assembly:\\s*UnityCli\\.UnityCliExtensionAssembly\\("${extensionIds[index]}"\\)\\s*\\]`));
  }
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

test("legacy project Context registries are absent while retained Tool owners stay on runtime-tool routes", async () => {
  const [runtimeDomain, dispatcher] = await Promise.all([
    readFile(runtimeBridgeDomainPath, "utf8"),
    readFile(toolDispatcherPath, "utf8"),
  ]);
  for (const path of [
    "unitypkg/Editor/ProjectContexts",
    "unitypkg/Editor/Contexts",
    "src/contexts",
    "skills/unitycli/references/custom-contexts.md",
  ]) await assert.rejects(access(path), { code: "ENOENT" });
  assert.match(runtimeDomain, /bridgeCommand: "catalog.player.describe"/);
  assert.match(runtimeDomain, /bridgeCommand: "catalog.player.run"/);
  assert.match(dispatcher, /if \(localMetadata\?\.handler === "bridge-runtime"\) \{[\s\S]*return dispatchResolved\(localMetadata, input\);[\s\S]*\}/);
});

test("generated C#/TS, contracts, schemas, scripts, product source, docs, and tests contain no removed per-Tool identifiers", async () => {
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
  const files = (await Promise.all([
    "unitypkg/Editor", "unitypkg/Tests", "extensions", "src", "contracts", "schemas", "scripts", "docs", "tests",
  ].map(sourceFiles))).flat();
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
