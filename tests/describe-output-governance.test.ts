import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { allRegisteredToolMetadata, findToolByName, type ResolvedToolMetadata } from "../src/tools/metadata/index.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/index.js";
import "../src/extensions/index.js";

const INTERNAL_SOURCE_PATTERN = /\b(CoplayDev|IvanMurzak|Aligns with|reference project|inspired by)\b/i;
const COMMAND_SAMPLE_PATTERN = /\b(?:unitycli\s+)?tools\s+run\s+[\w.-]+\b/i;
const LARGE_OUTPUT_PATTERN = /\b(pageInfo|cursor|nextCursor|plannedEffects|artifactDir|truncated)\b/i;

const P0_EXAMPLE_TOOLS = new Set([
  "debug.code-execute",
  "package.install",
  "package.remove",
  "package.deploy",
  "package.restore",
  "editor.compile",
  "build.run",
  "navmesh.bake",
  "navmesh.surface-bake",
  "test.cancel",
  "test.run"
]);

const P0_OUTPUT_SCHEMA_TOOLS = new Set([
  "debug.code-execute",
  "docs.search",
  "docs.get",
  "type-inspection.search",
  "build.run",
  "navmesh.bake",
  "navmesh.surface-bake",
  "test.cancel",
  "test.run"
]);

const LARGE_OUTPUT_TOOLS = new Set([
  "docs.search",
  "type-inspection.search",
  "debug.code-execute"
]);

function descriptorText(tool: ResolvedToolMetadata): string {
  return [
    tool.name,
    tool.summary,
    tool.description,
    JSON.stringify(tool.inputSchema),
    JSON.stringify(tool.outputSchema),
    JSON.stringify(tool.examples),
    JSON.stringify(tool.sideEffects),
    tool.playModeSupport,
    tool.supportStatus,
    tool.availabilityScope,
    tool.target,
    JSON.stringify(tool.requiresPackages)
  ].join("\n");
}

function assertNoInternalSourceText(subject: string, text: string): void {
  assert.doesNotMatch(text, INTERNAL_SOURCE_PATTERN, `${subject} must not expose internal reference-project wording`);
}

test("TS-local descriptor registry owns exactly the current package-local tools", () => {
  const names = allRegisteredToolMetadata().map((tool) => tool.name).sort();
  assert.deepEqual(names, [
    "asset.refresh", "asset.refresh-status", "build.player-status", "build.run", "debug.code-execute", "debug.code-flow-cancel", "debug.code-flow-run", "debug.code-flow-submit", "debug.code-flow-wait", "docs.get", "docs.search", "editor.compile", "editor.dialog-click",
    "editor.dialogs", "editor.play-mode-status", "editor.refresh", "editor.state", "navmesh.bake", "navmesh.bake-cancel",
    "navmesh.bake-status", "navmesh.surface-bake", "occlusion.bake", "occlusion.bake-cancel",
    "occlusion.bake-status", "package.bridge-remove", "package.deploy",
    "package.embed", "package.install", "package.install-status", "package.registry-clear", "package.registry-set", "package.remove", "package.remove-status",
    "package.resolve", "package.restore", "package.scoped-registry-add", "package.scoped-registry-remove",
    "player-runtime.console", "player-runtime.input-key", "player-runtime.input-pointer",
    "player-runtime.log", "player-runtime.state", "player-runtime.target-framerate-set", "player-runtime.time-scale-set", "player-runtime.ui-capture-element",
    "test.cancel", "test.run", "test.run-status", "type-inspection.get-member", "type-inspection.get-type", "type-inspection.search"
  ]);
  assert.equal(findToolByName("asset.refresh")?.handler, "hybrid");
});

test("offline describe descriptors do not expose internal reference-project wording", () => {
  for (const tool of allRegisteredToolMetadata()) {
    assertNoInternalSourceText(tool.name, descriptorText(tool));
  }

  for (const manifest of BUILTIN_EXTENSION_MANIFESTS) {
    assertNoInternalSourceText(manifest.id, JSON.stringify(manifest));
  }
});

test("high-risk TS-local metadata retains examples and internal output schemas", () => {
  for (const name of P0_EXAMPLE_TOOLS) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.ok(tool.examples?.length, `${name} must include at least one example`);
  }

  for (const name of P0_OUTPUT_SCHEMA_TOOLS) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.ok(tool.outputSchema, `${name} must retain internal outputSchema`);
  }
});

test("descriptions are call guidance, not duplicated summaries or embedded command examples", () => {
  for (const tool of allRegisteredToolMetadata()) {
    assert.ok(tool.description.trim(), `${tool.name} description must not be empty`);
    assert.notEqual(tool.description.trim(), tool.summary.trim(), `${tool.name} description must add call guidance beyond summary`);
    assert.doesNotMatch(tool.description, COMMAND_SAMPLE_PATTERN, `${tool.name} command examples belong in examples, not description`);
  }
});

test("debug.code-execute discovery keeps critical execution boundaries within its description budget", () => {
  const tool = findToolByName("debug.code-execute");
  assert.ok(tool);
  assert.match(tool.summary, /cannot be force-stopped by timeout/i);
  assert.match(tool.description, /synchronously compile and execute trusted C# 8/i);
  // 包裹声明必须自足：确切声明、唯一的预声明标识符与 body 的作用域事实，否则调用方无法预防 CS0136 一类失败。
  assert.match(tool.description, /the body of exactly `public static object Run\(string argsJson\)` inside `namespace UnityCli\.RuntimeCode \{ public static class Script \{ \} \}`/);
  assert.match(tool.description, /the wrapper declares no locals, so argsJson is the only pre-declared identifier/);
  assert.match(tool.description, /a name declared at its top level cannot be redeclared in a nested block, loop, lambda or local function \(CS0136\)/);
  assert.match(tool.description, /A source whose text contains `public static object Run\(` is placed in the class as-is instead/);
  assert.match(tool.description, /runtime\.returnValueTruncated plus runtime\.returnValueByteBudget state whether the returned value was cut/);
  assert.match(tool.description, /no capability reduction/i);
  assert.match(tool.description, /cannot interrupt Run/i);
  assert.match(tool.description, /outcome-unknown/i);
  assert.match(tool.description, /completionProbe is optional/i);
  assert.match(tool.description, /Dry-run reports source hash/i);
  // 该描述必须同时公布 typed partial outcome 的信封形状、30000 ms off-main 控制等待边界、
  // 确切包裹声明、返回值截断事实与默认引用集，因此预算从 1536 提升到 2304、再提升到 2688 UTF-8 字节；上限本身仍然被强制。
  assert.ok(Buffer.byteLength(tool.description, "utf8") <= 2688, "debug.code-execute description must remain within 2688 UTF-8 bytes");
  assert.match(tool.description, /every locatable assembly already loaded in the Editor AppDomain is referenced/);
  assert.match(tool.description, /error\.details\.outcome/i);
  assert.match(tool.description, /30000 ms/);
  assert.match(tool.description, /dispatch site whose creation result is answered before the pause starts/i);
});

test("large-output TS-local descriptors disclose output controls", () => {
  for (const name of LARGE_OUTPUT_TOOLS) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.match(descriptorText(tool), LARGE_OUTPUT_PATTERN, `${name} must disclose pagination, preview, planned effects, or artifact controls`);
  }
});

test("debug.code-flow-wait keeps its snapshot example honest and discloses the wait-window cost", () => {
  const tool = findToolByName("debug.code-flow-wait");
  assert.ok(tool);
  // “without waiting” 必须是真正的零长度窗口：省略 waitMs 走的是等到请求 deadline 的最长路径。
  const snapshot = tool.examples?.find((example) => {
    const input = example.input as { waitMs?: number } | undefined;
    return input?.waitMs === 0;
  });
  assert.ok(snapshot, "debug.code-flow-wait must keep an example that asks for waitMs 0");
  assert.match(snapshot.description, /without waiting/i);
  assert.match(snapshot.description, /E_DEBUG_FLOW_PENDING/);
  // 缺省语义与占档成本必须自足：否则调用方会把 140 s 的读请求当成一次快照。
  assert.match(tool.description, /waitMs 0 returns the current facts immediately/);
  assert.match(tool.description, /omitting waitMs waits until this request own deadline/);
  assert.match(tool.description, /other sessions' mutation requests cannot start until it returns/);
});

test("doctor private diagnostics read the immutable catalog owner and keep compile output bounded", async () => {
  const [diagnostics, registry, bridge] = await Promise.all([
    readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliDoctorDiagnostics.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/UnityCliBridge.cs", import.meta.url), "utf8"),
  ]);
  assert.match(diagnostics, /internal\.doctor\.business[\s\S]*internal\.doctor\.catalog[\s\S]*internal\.doctor\.compile/);
  assert.match(diagnostics, /maxScannedEntries = 5000[\s\S]*maxScanMilliseconds = 50[\s\S]*maxCompileErrorBytes = 64 \* 1024/);
  const doctorHandler = diagnostics.slice(diagnostics.indexOf("private static object DoctorCatalogDiagnostic"), diagnostics.indexOf("private static object DoctorCompileDiagnostic"));
  assert.match(doctorHandler, /ProjectToolCatalogSnapshotReader\.Read\(\)/);
  assert.match(doctorHandler, /providerCounts[\s\S]*publicationDiagnostics[\s\S]*DiscoveryPassCount[\s\S]*ProviderMetadataReadCount[\s\S]*AggregateSuccessLogCount/);
  assert.doesNotMatch(doctorHandler, /DiscoverProjectTools|RefreshProjectToolFingerprint|ProjectToolDescriptorData|rePinValue|blockedTools/);
  assert.doesNotMatch(registry, /ProjectToolCatalogDiagnosticState|ProjectToolCatalogBlockedDiagnostic|projectToolCatalogDiagnostics/);
  assert.doesNotMatch(bridge, /projectToolCatalogDiagnostics/);
});
