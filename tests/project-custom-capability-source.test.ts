import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const toolInterfacePath = "unitypkg/Editor/ProjectTools/IUnityCliTool.cs";
const toolRegistryPath = "unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs";
const contextInterfacePath = "unitypkg/Editor/ProjectTools/IUnityCliTool.cs";
const contextRegistryPath = "unitypkg/Editor/ProjectContexts/ProjectContextRegistry.cs";
const runtimeExecutorPath = "unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs";
const contextBridgeDomainPath = "src/bridge/domains/context.ts";
const runtimeBridgeDomainPath = "src/bridge/domains/runtime-tool.ts";
const toolDispatcherPath = "src/tools/dispatcher.ts";

test("typed Tool input cannot become selector or execution flags", async () => {
  const dispatcher = await readFile(toolDispatcherPath, "utf8");
  assert.match(dispatcher, /const dispatchFlags = withStructuredInput\(flags, parsed\.input\)/);
  assert.match(dispatcher, /TOOL_CONTROL_FLAGS[\s\S]*"instance"[\s\S]*"runtime"[\s\S]*"timeout-ms"/);
  assert.doesNotMatch(dispatcher, /function mergeInputFlags/);
  assert.doesNotMatch(dispatcher, /Object\.entries\(input\)[\s\S]*merged\[flag\]/);
});

test("project custom Tool uses async typed execution and a complete direct completion contract", async () => {
  const [toolInterface, registry] = await Promise.all([
    readFile(toolInterfacePath, "utf8"),
    readFile(toolRegistryPath, "utf8")
  ]);

  assert.match(toolInterface, /ValueTask<TRes>\s+ExecuteAsync\(TReq request, UnityCliProjectToolContext ctx, CancellationToken ct/);
  assert.match(toolInterface, /ToolCompletionDescriptor\s+Completion/);
  assert.match(registry, /implementationType\.FullName \+ "\.ExecuteAsync"/);
  assert.match(registry, /custom\.CompletionDriverId, mutation \? "required" : "none", mutation/);
  assert.doesNotMatch(registry, /"allowed",\s*"ordinary",\s*custom\.CompletionDriverId/);
  assert.match(registry, /\{ "examples", definition\.Examples \}/);
  assert.match(registry, /\{ "defaultTimeoutMs", definition\.DefaultTimeoutMs \}/);
  assert.match(registry, /\{ "completionContract", CompletionContractData\(definition\.CompletionContract\) \}/);
  assert.match(registry, /\{ "internalDescriptorFingerprint", definition\.Fingerprint \}/);
  assert.match(registry, /data\["sideEffects"\] = definition\.SideEffects/);
  assert.match(registry, /data\["permissions"\] = definition\.Permissions/);
  assert.match(registry, /data\["requiresPackages"\] = definition\.RequiresPackages/);
  assert.match(registry, /unity-cli-project-tool-fingerprint-v2/);
  assert.match(registry, /baseline = FormatProjectToolFingerprint\(definition\.Fingerprint\)/);
  assert.match(registry, /SaveProjectToolExpectedFingerprint\(definition\.Name, baseline\)/);
  assert.match(registry, /ParseProjectToolPinnedFingerprint\("descriptor", definition\.PinnedFingerprint\)/);
  assert.match(registry, /ParseProjectToolPinnedFingerprint\("project-baseline", baseline\)/);
  assert.match(registry, /CompletionFingerprintMaterial\(definition\.CompletionContract\)/);
  assert.match(registry, /UnityCliSerializer\.Serialize\(definition\.Examples/);
  assert.match(registry, /string\.Equals\(definition\.Ownership, "project", StringComparison\.Ordinal\)/);
  assert.doesNotMatch(registry, /string\.Equals\(definition\.Source, "project", StringComparison\.Ordinal\)/);
});

test("project custom Context uses cancellation-aware ValueTask reads and public examples", async () => {
  const [contextInterface, registry] = await Promise.all([
    readFile(contextInterfacePath, "utf8"),
    readFile(contextRegistryPath, "utf8")
  ]);

  assert.match(contextInterface, /ValueTask<TRes>\s+ReadAsync\(TReq request, UnityCliProjectToolContext ctx, CancellationToken ct/);
  assert.match(registry, /implementationType\.FullName \+ "\.ReadAsync"/);
  assert.match(registry, /\{ "examples", definition\.Examples \}/);
  assert.match(registry, /data\["requiresPackages"\] = definition\.RequiresPackages/);
  assert.match(registry, /unity-cli-project-context-fingerprint-v2/);
  assert.match(registry, /baseline = FormatProjectContextFingerprint\(definition\.Fingerprint\)/);
  assert.match(registry, /SaveProjectContextExpectedFingerprint\(definition\.Name, baseline\)/);
  assert.match(registry, /ParseProjectContextPinnedFingerprint\("descriptor", definition\.PinnedFingerprint\)/);
  assert.match(registry, /ParseProjectContextPinnedFingerprint\("project-baseline", baseline\)/);
  assert.match(registry, /UnityCliSerializer\.Serialize\(definition\.Examples/);
  assert.match(registry, /string\.Equals\(definition\.Ownership, "project", StringComparison\.Ordinal\)/);
  assert.doesNotMatch(registry, /string\.Equals\(definition\.Source, "project", StringComparison\.Ordinal\)/);
  assert.doesNotMatch(registry, /if \(definition\.Source == "project"\)/);
  assert.doesNotMatch(registry, /OrderBy\(context => context\.Source/);
  assert.doesNotMatch(registry, /RefreshBehavior|refreshBehavior/);
});

test("project Tool and Context v2 pins use a single hard-block migration path", async () => {
  const [toolRegistry, contextRegistry] = await Promise.all([
    readFile(toolRegistryPath, "utf8"),
    readFile(contextRegistryPath, "utf8")
  ]);

  for (const [registry, kind] of [[toolRegistry, "Tool"], [contextRegistry, "Context"]] as const) {
    assert.match(registry, new RegExp(`private sealed class Project${kind}PinnedFingerprint`));
    assert.match(registry, /\^\(\[\^:\]\+\):\(\[0-9a-fA-F\]\{64\}\)\$/);
    assert.match(registry, /bare-sha256/);
    assert.match(registry, /algorithm-mismatch/);
    assert.match(registry, /Format = "malformed"/);
    assert.match(registry, /Blocked = !matches/);
    assert.match(registry, /FingerprintStatus = matches \? "match" : "blocked"/);
    assert.doesNotMatch(registry, /FingerprintMode|FingerprintDefaultMode|FINGERPRINT_MODE|warnCount|fingerprint_warn/);
    assert.match(registry, /currentAlgorithm/);
    assert.match(registry, /actualSha256/);
    assert.match(registry, /pinnedOwner/);
    assert.match(registry, /pinnedFormat/);
    assert.match(registry, /rePinValue/);
    assert.match(registry, /rePinSteps/);
    assert.doesNotMatch(registry, /use warn mode when policy allows/);
  }

  assert.match(toolRegistry, /Set ToolDescriptor\.Fingerprint to/);
  assert.match(contextRegistry, /Set ContextDescriptor\.Fingerprint to/);
});

test("Tool, Context, and runtime routes expose no fingerprint policy selector", async () => {
  const sources = await Promise.all([
    readFile(toolRegistryPath, "utf8"),
    readFile(contextRegistryPath, "utf8"),
    readFile(runtimeExecutorPath, "utf8"),
    readFile(contextBridgeDomainPath, "utf8"),
    readFile(runtimeBridgeDomainPath, "utf8")
  ]);

  for (const source of sources) {
    assert.doesNotMatch(source, /FingerprintMode|fingerprintMode|fingerprint-mode|FINGERPRINT_MODE|warnCount|fingerprint_warn/);
  }
  assert.match(sources[2], /RefreshProjectToolFingerprint\(definition\)/);
  assert.match(sources[2], /ProjectToolFingerprintRePinSteps\(definition\)/);
});

test("internal project Tool fingerprint remains bridge-only", async () => {
  const [toolRegistry, publicCatalog, staticCapture] = await Promise.all([
    readFile(toolRegistryPath, "utf8"),
    readFile("src/tools/catalog.ts", "utf8"),
    readFile("scripts/capture-static-capability-catalog.mjs", "utf8")
  ]);

  assert.match(toolRegistry, /\{ "internalDescriptorFingerprint", definition\.Fingerprint \}/);
  const publicDescriptor = publicCatalog.slice(publicCatalog.indexOf("export function publicToolDescriptor"), publicCatalog.indexOf("export function publicOutputSchema"));
  assert.doesNotMatch(publicDescriptor, /internalDescriptorFingerprint/);
  assert.match(staticCapture, /return publicToolDescriptor\(metadata\)/);
});

test("published custom capability examples match the current async API", async () => {
  const [toolDocs, contextDocs] = await Promise.all([
    readFile("skills/unity_cli/references/custom-tools.md", "utf8"),
    readFile("skills/unity_cli/references/custom-contexts.md", "utf8")
  ]);

  assert.match(toolDocs, /ValueTask<Result> ExecuteAsync\(/);
  assert.match(toolDocs, /Completion = new ToolCompletionDescriptor/);
  assert.doesNotMatch(toolDocs, /public Result Execute\(/);
  assert.match(contextDocs, /ValueTask<Result> ReadAsync\(/);
  assert.doesNotMatch(contextDocs, /public Result Read\(/);
});
