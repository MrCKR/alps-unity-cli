import assert from "node:assert/strict";
import test from "node:test";
import { allRegisteredToolMetadata, type ResolvedToolMetadata } from "../src/tools/metadata/index.js";
import { builtinContextDescriptors } from "../src/context/registry.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/index.js";
import "../src/extensions/index.js";

const INTERNAL_SOURCE_PATTERN = /\b(CoplayDev|IvanMurzak|Aligns with|reference project|inspired by)\b/i;
const COMMAND_SAMPLE_PATTERN = /\b(?:unity_cli\s+)?tools\s+run\s+[\w.-]+\b/i;
const LARGE_OUTPUT_PATTERN = /\b(pageInfo|cursor|nextCursor|nextPath|nextOffset|truncated|childrenTruncated|artifact|inlineBase64|imageBase64)\b/i;

const P0_EXAMPLE_TOOLS = new Set([
  "asset.preview",
  "profiler.start",
  "profiler.stop",
  "profiler.pause",
  "profiler.module-enable",
  "profiler.clear",
  "profiler.load",
  "profiler.frame-debugger.enable",
  "profiler.frame-debugger.disable",
  "package.embed",
  "package.resolve"
]);

const P0_OUTPUT_SCHEMA_TOOLS = new Set([
  "asset.preview",
  "profiler.start",
  "profiler.stop",
  "profiler.pause",
  "profiler.clear",
  "profiler.load",
  "profiler.frame-debugger.enable",
  "profiler.frame-debugger.disable",
  "package.embed",
  "package.resolve"
]);

const P1_EXTENSION_RISK_TOOLS = new Set([
  "probuilder.delete-faces",
  "probuilder.combine-meshes",
  "probuilder.merge-objects",
  "cinemachine.extension",
  "urp.feature"
]);

const LARGE_OUTPUT_TOOLS = new Set([
  "object.get-data",
  "asset.get-data",
  "component.get",
  "scene.hierarchy",
  "prefab.hierarchy",
  "profiler.hierarchy",
  "profiler.frame-debugger.events",
  "camera.screenshot",
  "camera.multiview",
  "screenshot.capture",
  "screenshot.scene-view",
  "screenshot.game-view",
  "screenshot.isolated-object",
  "screenshot.orbit",
  "screenshot.surround"
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

test("describe descriptors do not expose internal reference-project wording", () => {
  for (const tool of allRegisteredToolMetadata()) {
    assertNoInternalSourceText(tool.name, descriptorText(tool));
  }

  for (const context of builtinContextDescriptors()) {
    assertNoInternalSourceText(context.name, JSON.stringify(context));
  }

  for (const manifest of BUILTIN_EXTENSION_MANIFESTS) {
    assertNoInternalSourceText(manifest.id, JSON.stringify(manifest));
  }
});

test("P0 and P1 high-risk describe descriptors include examples and output schemas", () => {
  const tools = new Map(allRegisteredToolMetadata().map((tool) => [tool.name, tool]));

  for (const name of P0_EXAMPLE_TOOLS) {
    const tool = tools.get(name);
    assert.ok(tool, `${name} should exist`);
    assert.ok(tool.examples?.length, `${name} must include at least one example`);
  }

  for (const name of P0_OUTPUT_SCHEMA_TOOLS) {
    const tool = tools.get(name);
    assert.ok(tool, `${name} should exist`);
    assert.ok(tool.outputSchema, `${name} must expose outputSchema`);
  }

  for (const name of P1_EXTENSION_RISK_TOOLS) {
    const tool = tools.get(name);
    assert.ok(tool, `${name} should exist`);
    assert.ok(tool.examples?.length, `${name} extension risk tool must include examples`);
    assert.ok(tool.outputSchema, `${name} extension risk tool must expose outputSchema`);
  }
});

test("descriptions are call guidance, not duplicated summaries or embedded command examples", () => {
  for (const tool of allRegisteredToolMetadata()) {
    assert.ok(tool.description.trim(), `${tool.name} description must not be empty`);
    assert.notEqual(tool.description.trim(), tool.summary.trim(), `${tool.name} description must add call guidance beyond summary`);
    assert.doesNotMatch(tool.description, COMMAND_SAMPLE_PATTERN, `${tool.name} command examples belong in examples, not description`);
  }
});

test("local context describe entries disclose read-only observation boundaries", () => {
  for (const context of builtinContextDescriptors()) {
    assert.ok(context.description.trim(), `${context.name} context description must not be empty`);
    const text = context.description.toLowerCase();
    const readSignal = /read|snapshot|observes|observation|without|does not|no unity connection|available|list|metadata/.test(text);
    assert.ok(readSignal, `${context.name} description should read as observation/read-only guidance`);
    const mutationWords = ["write", "delete", "create", "modify", "save", "open", "close", "refresh", "trigger"];
    for (const word of mutationWords) {
      const match = new RegExp(`\\b${word}\\b`, "i").exec(context.description);
      if (!match) continue;
      const sentenceStart = context.description.lastIndexOf(".", match.index) + 1;
      const sentenceEnd = context.description.indexOf(".", match.index);
      const sentence = context.description.slice(sentenceStart, sentenceEnd === -1 ? undefined : sentenceEnd + 1);
      const previousChar = match.index > 0 ? context.description[match.index - 1] : "";
      if (previousChar === ".") continue;
      assert.match(sentence, /without|does not|not|no stable public API/i, `${context.name} mutation-like word '${word}' must be in an explicit no-side-effect sentence`);
    }
  }
});

test("large-output describe descriptors disclose output controls", () => {
  const tools = new Map(allRegisteredToolMetadata().map((tool) => [tool.name, tool]));
  for (const name of LARGE_OUTPUT_TOOLS) {
    const tool = tools.get(name);
    assert.ok(tool, `${name} should exist`);
    assert.match(descriptorText(tool), LARGE_OUTPUT_PATTERN, `${name} must disclose pagination, truncation, artifact or inline output controls`);
  }
});
