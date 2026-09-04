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
  "code.exec",
  "package.install",
  "package.remove",
  "package.deploy",
  "package.restore",
  "editor.compile",
  "build.run",
  "navmesh.bake",
  "navmesh.surface.bake",
  "test.cancel",
  "test.run"
]);

const P0_OUTPUT_SCHEMA_TOOLS = new Set([
  "code.exec",
  "docs.search",
  "docs.get",
  "reflect.search",
  "build.run",
  "navmesh.bake",
  "navmesh.surface.bake",
  "test.cancel",
  "test.run"
]);

const LARGE_OUTPUT_TOOLS = new Set([
  "docs.search",
  "reflect.search",
  "code.exec"
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
    "asset.refresh", "asset.refresh.status", "build.player.status", "build.run", "code.exec", "docs.get", "docs.search", "editor.compile", "editor.dialog.click",
    "editor.dialogs", "editor.play-mode.status", "editor.refresh", "editor.state", "navmesh.bake", "navmesh.bake-cancel",
    "navmesh.bake-status", "navmesh.surface.bake", "occlusion.bake", "occlusion.bake-cancel",
    "occlusion.bake-status", "package.bridge-remove", "package.deploy",
    "package.embed", "package.install", "package.install.status", "package.registry.clear", "package.registry.set", "package.remove", "package.remove.status",
    "package.resolve", "package.restore", "package.scoped-registry.add", "package.scoped-registry.remove", "package.update.status",
    "reflect.member", "reflect.search", "reflect.type", "runtime.console", "runtime.input.key", "runtime.input.pointer",
    "runtime.log", "runtime.state", "runtime.target-framerate.set", "runtime.time-scale.set", "runtime.ui.capture-element",
    "test.cancel", "test.run", "test.run.status"
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

test("code.exec discovery keeps critical execution boundaries within its description budget", () => {
  const tool = findToolByName("code.exec");
  assert.ok(tool);
  assert.match(tool.summary, /cannot be force-stopped by timeout/i);
  assert.match(tool.description, /synchronously compile and execute trusted C# 8/i);
  assert.match(tool.description, /no capability reduction/i);
  assert.match(tool.description, /cannot interrupt Run/i);
  assert.match(tool.description, /outcome-unknown/i);
  assert.match(tool.description, /completionProbe is optional/i);
  assert.match(tool.description, /Dry-run reports source hash/i);
  assert.ok(Buffer.byteLength(tool.description, "utf8") <= 1024, "code.exec description must remain within 1024 UTF-8 bytes");
});

test("large-output TS-local descriptors disclose output controls", () => {
  for (const name of LARGE_OUTPUT_TOOLS) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.match(descriptorText(tool), LARGE_OUTPUT_PATTERN, `${name} must disclose pagination, preview, planned effects, or artifact controls`);
  }
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
