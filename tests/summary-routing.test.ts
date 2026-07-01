import assert from "node:assert/strict";
import test from "node:test";
import { handleToolsCommand, type ToolsRuntime } from "../src/tools.js";
import { allRegisteredToolMetadata } from "../src/tools/metadata/index.js";
import { builtinContextDescriptors } from "../src/context/registry.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/index.js";
import "../src/extensions/index.js";

const requestId = "test_summary_routing_001";
const emptyRuntime: ToolsRuntime = {};

const HIGH_RISK_GROUP_KEYWORDS: Record<string, string[]> = {
  reflection: ["live", "method"],
  reflect: ["offline", "cache"],
  type: ["schema"],
  camera: ["create", "lens", "screenshot"],
  graphics: ["pipeline", "bake", "skybox", "stats"],
  editor: ["state", "playmode", "compile", "selection"],
  scene: ["hierarchy", "load", "save", "validation"],
  screenshot: ["game view", "scene view", "isolated"],
  asset: ["search", "refresh", "preview"],
  ui: ["ugui", "ui toolkit", "visual"],
  physics: ["settings", "ray", "rigidbod"],
  shader: ["source", "structured", "create"],
  texture: ["sprite", "import", "generation"],
  prefab: ["hierarchy", "instantiate", "stage"],
  go: ["gameobject", "create", "parent"]
};

const ALIAS_TOOLS = ["editor.console", "scene.screenshot", "screenshot.capture", "editor.refresh"];
const GENERIC_PATTERNS = [/\bvarious\b/i, /\butilities\b/i, /\bhelpers\b/i, /\bmanage things\b/i, /^run\s+[\w.-]+\.?$/i, /^manage\s+\w+\.?$/i, /\.\.\./];

function wordCount(text: string): number {
  return text.trim().split(/\s+/).filter(Boolean).length;
}

function assertRouteSummary(name: string, summary: string): void {
  assert.ok(summary.trim(), `${name} summary must not be empty`);
  assert.ok(wordCount(summary) <= 32, `${name} summary is too long: ${wordCount(summary)} words`);
  for (const pattern of GENERIC_PATTERNS) {
    assert.doesNotMatch(summary, pattern, `${name} summary is not route-specific: ${summary}`);
  }
}

test("tools group summaries are route-specific and cover high-risk group boundaries", async () => {
  const result = await handleToolsCommand("group", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { groups: Array<{ name: string; summary: string }> };
  assert.ok(data.groups.length > 0);

  for (const group of data.groups) {
    assertRouteSummary(`group ${group.name}`, group.summary);
    const keywords = HIGH_RISK_GROUP_KEYWORDS[group.name] ?? [];
    const lower = group.summary.toLowerCase();
    for (const keyword of keywords) {
      assert.ok(lower.includes(keyword), `group ${group.name} summary should include route keyword ${keyword}: ${group.summary}`);
    }
  }

  const reflection = data.groups.find((group) => group.name === "reflection")!;
  assert.doesNotMatch(reflection.summary, /schema/i, "reflection group must not claim type schema generation");

  const controller = data.groups.find((group) => group.name === "controller")!;
  assert.match(controller.summary, /add\/read transitions/i, "controller group should distinguish adding/reading transitions from editing existing ones");
  assert.match(controller.summary, /generic object paths/i, "controller group should route unsupported existing transition edits to generic object paths");
});

test("tool summaries are concise route fields and alias tools point to main entries", () => {
  const tools = allRegisteredToolMetadata();
  assert.ok(tools.length > 0);

  for (const tool of tools) {
    assertRouteSummary(tool.name, tool.summary);
  }

  for (const name of ALIAS_TOOLS) {
    const tool = tools.find((candidate) => candidate.name === name);
    assert.ok(tool, `${name} should exist`);
    const routeText = `${tool.summary} ${tool.description}`;
    assert.match(routeText, /alias|prefer/i, `${name} should disclose alias/default-entry routing`);
  }

  assert.match(tools.find((tool) => tool.name === "shader.read")!.summary, /source|text/i);
  assert.match(tools.find((tool) => tool.name === "shader.get-data")!.summary, /structured|properties|pass/i);
  assert.match(tools.find((tool) => tool.name === "asset.get-data")!.summary, /asset/i);
  assert.match(tools.find((tool) => tool.name === "object.get-data")!.summary, /fallback|UnityEngine\.Object|Unity Object/i);
  assert.match(tools.find((tool) => tool.name === "component.get")!.summary, /Component/i);
  assert.match(tools.find((tool) => tool.name === "go.inspect")!.summary, /GameObject/i);
  assert.match(tools.find((tool) => tool.name === "reflection.method.call")!.summary, /requires --yes|arbitrary code|executes/i);
  assert.match(tools.find((tool) => tool.name === "type.schema")!.summary, /schema/i);

  const transitionAdd = tools.find((tool) => tool.name === "controller.add-transition")!;
  assert.match(`${transitionAdd.summary} ${transitionAdd.description}`, /new transition/i);
  assert.match(`${transitionAdd.summary} ${transitionAdd.description}`, /existing transition edits|does not edit an existing transition|no dedicated/i);

  const controllerInfo = tools.find((tool) => tool.name === "controller.get-info")!;
  assert.match(`${controllerInfo.summary} ${controllerInfo.description}`, /existing transitions/i);
  assert.match(`${controllerInfo.summary} ${controllerInfo.description}`, /does not modify|no dedicated existing-transition edit/i);
});

test("context discovery descriptions are route-specific and summary-free", () => {
  const contexts = builtinContextDescriptors();
  assert.ok(contexts.length > 0);
  for (const context of contexts) {
    assert.ok(context.description.trim(), `${context.name} description must not be empty`);
    assert.ok(wordCount(context.description) <= 70, `${context.name} context discovery description is too long`);
    for (const pattern of GENERIC_PATTERNS) {
      assert.doesNotMatch(context.description, pattern, `${context.name} context description is not route-specific`);
    }
  }
  assert.ok(contexts.some((context) => context.name === "scene.gameobject" && /component\.get|object\.get-data/.test(context.description)));
});

test("extension manifest and contribution descriptions are route-specific", () => {
  assert.ok(BUILTIN_EXTENSION_MANIFESTS.length > 0);
  for (const manifest of BUILTIN_EXTENSION_MANIFESTS) {
    assert.ok(manifest.description.trim(), `${manifest.id} manifest description missing`);
    assert.ok(wordCount(manifest.description) <= 32, `${manifest.id} manifest description too long`);
    for (const pattern of GENERIC_PATTERNS) {
      assert.doesNotMatch(manifest.description, pattern, `${manifest.id} manifest description is not route-specific`);
    }
    for (const context of manifest.contributions.contexts ?? []) {
      assert.ok(context.description.trim(), `${manifest.id}:${context.key} context description missing`);
      assert.ok(wordCount(context.description) <= 32, `${manifest.id}:${context.key} context description too long`);
    }
  }
});
