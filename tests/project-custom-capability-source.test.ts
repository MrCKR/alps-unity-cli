import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

const toolInterfacePath = "unitypkg/Editor/ProjectTools/IUnityCliTool.cs";
const toolRegistryPath = "unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs";
const runtimeExecutorPath = "unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs";
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
    readFile(toolRegistryPath, "utf8"),
  ]);
  assert.match(toolInterface, /ValueTask<TRes>\s+ExecuteAsync\(TReq request, UnityCliProjectToolContext ctx, CancellationToken ct/);
  assert.match(toolInterface, /ToolCompletionDescriptor\s+Completion/);
  assert.doesNotMatch(toolInterface, /IUnityCliContext|ContextDescriptor|ContextExample/);
  assert.match(registry, /implementationType\.FullName \+ "\.ExecuteAsync"/);
  assert.match(registry, /custom\.CompletionDriverId, mutation \? "required" : "none", mutation/);
  assert.match(registry, /\{ "completionContract", CompletionContractData\(definition\.CompletionContract\) \}/);
  assert.match(registry, /\{ "internalDescriptorFingerprint", definition\.Fingerprint \}/);
  assert.match(registry, /unity-cli-project-tool-fingerprint-v2/);
  assert.match(registry, /CompletionFingerprintMaterial\(definition\.CompletionContract\)/);
  assert.match(registry, /string\.Equals\(definition\.Ownership, "project", StringComparison\.Ordinal\)/);
  assert.doesNotMatch(registry, /FingerprintMode|fingerprintMode|fingerprint-mode|FINGERPRINT_MODE|warnCount|fingerprint_warn/);
});

test("legacy project Context registries and authoring docs are absent", async () => {
  for (const path of [
    "unitypkg/Editor/ProjectContexts",
    "unitypkg/Editor/Contexts",
    "src/contexts",
    "src/bridge/domains/context.ts",
    "skills/unity_cli/references/custom-contexts.md",
  ]) await assert.rejects(access(path), { code: "ENOENT" });
});

test("Tool and runtime routes expose no fingerprint policy selector", async () => {
  const sources = await Promise.all([
    readFile(toolRegistryPath, "utf8"),
    readFile(runtimeExecutorPath, "utf8"),
    readFile(runtimeBridgeDomainPath, "utf8"),
  ]);
  for (const source of sources) assert.doesNotMatch(source, /FingerprintMode|fingerprintMode|fingerprint-mode|FINGERPRINT_MODE|warnCount|fingerprint_warn/);
  assert.match(sources[1], /RefreshProjectToolFingerprint\(definition\)/);
  assert.match(sources[1], /ProjectToolFingerprintRePinSteps\(definition\)/);
});

test("internal project Tool fingerprint remains bridge-only", async () => {
  const [toolRegistry, publicCatalog, staticCapture] = await Promise.all([
    readFile(toolRegistryPath, "utf8"),
    readFile("src/tools/catalog.ts", "utf8"),
    readFile("scripts/capture-static-capability-catalog.mjs", "utf8"),
  ]);
  assert.match(toolRegistry, /\{ "internalDescriptorFingerprint", definition\.Fingerprint \}/);
  const publicDescriptor = publicCatalog.slice(publicCatalog.indexOf("export function publicToolDescriptor"), publicCatalog.indexOf("export function publicOutputSchema"));
  assert.doesNotMatch(publicDescriptor, /internalDescriptorFingerprint/);
  assert.doesNotMatch(staticCapture, /publicToolDescriptor/);
  assert.match(staticCapture, /function toStaticToolDescriptor[\s\S]*\.\.\.metadata[\s\S]*source: descriptor\.source/);
});

test("published custom Tool example matches the current async API", async () => {
  const toolDocs = await readFile("skills/unity_cli/references/custom-tools.md", "utf8");
  assert.match(toolDocs, /ValueTask<Result> ExecuteAsync\(/);
  assert.match(toolDocs, /Completion = new ToolCompletionDescriptor/);
  assert.doesNotMatch(toolDocs, /public Result Execute\(/);
});
