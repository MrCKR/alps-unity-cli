import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import test from "node:test";
import { GROUP_SUMMARIES } from "../src/tools/tools/group-summaries.js";
import { GENERATED_TOOL_COMPLETION_CONTRACTS } from "../src/tools/.generated-completion-registry.js";

const HOOK_GROUP = "method-hook";
const MUTATIONS = ["method-hook.install", "method-hook.replace", "method-hook.enable"];
const SESSION_MUTATIONS = ["method-hook.disable", "method-hook.remove"];
const READS = ["method-hook.list", "method-hook.info", "method-hook.events"];
const HOOK_TOOLS = [...MUTATIONS, ...SESSION_MUTATIONS, ...READS];
const FROZEN_SUMMARY = "Live Editor method hooks: install/replace, enable/disable, remove, list/info, and bounded events for observing calls, modifying behavior, and executing C# callbacks.";

interface InventoryEntry {
  name: string;
  group: string;
  descriptorOwner: string;
  source: string;
  destructiveHint: boolean;
}

async function inventory(): Promise<InventoryEntry[]> {
  const moduleUrl = pathToFileURL(join(process.cwd(), "scripts", "lib", "public-tool-inventory.mjs")).href;
  const module = await import(moduleUrl) as { collectPublicToolInventory(root: string): InventoryEntry[] };
  return module.collectPublicToolInventory(process.cwd());
}

async function source(path: string): Promise<string> {
  return readFile(path, "utf8");
}

test("method-hook group is discoverable through the frozen route summary and exact tool names", async () => {
  assert.equal(GROUP_SUMMARIES[HOOK_GROUP], FROZEN_SUMMARY);
  assert.ok(FROZEN_SUMMARY.trim().split(/\s+/).length <= 32);

  const entries = await inventory();
  const hookTools = entries.filter((entry) => entry.group === HOOK_GROUP);
  assert.deepEqual(hookTools.map((entry) => entry.name).sort(), [...HOOK_TOOLS].sort());
  for (const tool of hookTools) {
    assert.equal(tool.descriptorOwner, "built-in", tool.name);
    assert.equal(tool.source.startsWith(`unitypkg/Editor/Tools/${HOOK_GROUP}/`), true, tool.source);
  }
  for (const name of HOOK_TOOLS) {
    const found = entries.find((entry) => entry.name === name);
    assert.ok(found, `${name} must exist in the canonical inventory`);
  }
});

test("method-hook tools declare their risk metadata exactly once per source file", async () => {
  for (const name of MUTATIONS) {
    const text = await source(`unitypkg/Editor/Tools/${HOOK_GROUP}/${name.slice(HOOK_GROUP.length + 1)}.cs`);
    assert.match(text, /DestructiveHint = true/, `${name} must declare destructiveHint`);
    assert.match(text, /SupportsDryRun = true/, `${name} must support dry-run`);
    assert.match(text, /Permissions = new\[\] \{ "execute-user-code" \}/, `${name} must declare the execute-user-code permission`);
    assert.match(text, /Target = "editor"/, `${name} must target the editor`);
    assert.match(text, /PlayModeSupport = "supported"/, `${name} must remain usable in Play Mode`);
    assert.match(text, /ctx\.CommitEffect\(\)/, `${name} must cross the single effect barrier`);
  }
  for (const name of SESSION_MUTATIONS) {
    const text = await source(`unitypkg/Editor/Tools/${HOOK_GROUP}/${name.slice(HOOK_GROUP.length + 1)}.cs`);
    assert.doesNotMatch(text, /DestructiveHint = true/, `${name} must not claim arbitrary-code destruction`);
    assert.match(text, /SupportsDryRun = true/, `${name} must support dry-run`);
    assert.match(text, /SideEffects = new\[\] \{ "method-patch", "session-definition" \}/, `${name} must declare its real side effects`);
    assert.doesNotMatch(text, /Permissions = new\[\] \{ "execute-user-code" \}/, `${name} must not require the callback permission`);
    assert.match(text, /ctx\.CommitEffect\(\)/, `${name} must cross the single effect barrier`);
  }
  for (const name of READS) {
    const text = await source(`unitypkg/Editor/Tools/${HOOK_GROUP}/${name.slice(HOOK_GROUP.length + 1)}.cs`);
    assert.doesNotMatch(text, /DestructiveHint = true/, `${name} must stay read-only`);
    assert.doesNotMatch(text, /SideEffects = new\[\] \{/, `${name} must declare no side effects`);
    assert.doesNotMatch(text, /ctx\.CommitEffect\(\)/, `${name} must never cross a mutation barrier`);
  }
});

test("method-hook completion contracts match the observed-state and atomic execution classes", async () => {
  const byName = new Map(GENERATED_TOOL_COMPLETION_CONTRACTS.map((entry) => [entry.toolName, entry]));
  for (const name of [...MUTATIONS, ...SESSION_MUTATIONS]) {
    const entry = byName.get(name);
    assert.ok(entry, `${name} completion contract`);
    assert.equal(entry.executionClass, "observed-state", name);
    assert.equal(entry.mutation, true, `${name} must be classified as a mutation`);
    assert.equal(entry.completionDriverId, "observed-terminal-readback-v1", name);
    assert.equal(entry.journalPolicy, "required", name);
  }
  for (const name of READS) {
    const entry = byName.get(name);
    assert.ok(entry, `${name} completion contract`);
    assert.equal(entry.executionClass, "atomic", name);
    assert.equal(entry.mutation, false, `${name} must not be classified as a mutation`);
    assert.equal(entry.completionDriverId, "atomic-typed-result-v1", name);
    assert.equal(entry.journalPolicy, "none", name);
  }
});

test("method-hook command contract reuses the same live catalog entry", async () => {
  const contract = JSON.parse(await readFile("contracts/command-contract.json", "utf8")) as {
    commands: Array<{ bridgeCommand: string; group: string; command: string; handler_type: string; csharp_handler: string }>;
  };
  for (const name of HOOK_TOOLS) {
    const entry = contract.commands.find((command) => command.bridgeCommand === name);
    assert.ok(entry, `${name} command contract entry`);
    assert.equal(entry.group, HOOK_GROUP, name);
    assert.equal(entry.command, name.slice(HOOK_GROUP.length + 1), `${name} command name projection`);
    assert.equal(entry.handler_type, "bridge", name);
    assert.equal(entry.csharp_handler, "present", name);
  }
});

test("package closure declares the hook backend dependency boundary", async () => {
  const contract = JSON.parse(await readFile("contracts/package-assembly-closure.json", "utf8")) as {
    hookBackend?: { assemblyName: string; typeName: string; ownerId: string; asmdefPath: string; precompiledReferences: string[]; references: string[] };
    bundledAssemblies: Array<{ fileName: string; assemblyName: string; sha256: string }>;
  };
  const backend = contract.hookBackend;
  assert.ok(backend, "hookBackend closure declaration");
  assert.equal(backend!.assemblyName, "UnityCli.Hooks.Editor");
  assert.equal(backend!.typeName, "UnityCli.Hooks.MethodHookHarmonyBackend");
  assert.deepEqual(backend!.precompiledReferences, ["0Harmony.dll"]);
  assert.deepEqual(backend!.references, ["UnityCli.Editor"]);

  const asmdef = JSON.parse(await readFile(join("unitypkg", backend!.asmdefPath), "utf8")) as {
    name: string; includePlatforms: string[]; overrideReferences: boolean; precompiledReferences: string[]; autoReferenced: boolean;
  };
  assert.equal(asmdef.name, backend!.assemblyName);
  assert.deepEqual(asmdef.includePlatforms, ["Editor"]);
  assert.equal(asmdef.overrideReferences, true);
  assert.deepEqual(asmdef.precompiledReferences, ["0Harmony.dll"]);
  assert.equal(asmdef.autoReferenced, false);

  const harmony = contract.bundledAssemblies.find((entry) => entry.assemblyName === "0Harmony");
  assert.ok(harmony, "bundled Harmony dependency");
  assert.equal(harmony!.sha256, "7b9e756306fa3d7620e02a857c8927a6ab04973f9bd8a77d3866700a6deac55c");
  const backendSource = await source(join("unitypkg", "Hooks", "MethodHookHarmonyBackend.cs"));
  assert.match(backendSource, new RegExp(backend!.ownerId.replace(".", "\\.")));

  // 插件导入投影：契约声明必须与随包 .meta 和运行后置核对一致。
  const requirements = backend as unknown as { pluginAssetRelativePath: string; pluginImporterRequirements: { compatibleWithAnyPlatform: boolean; compatibleWithEditor: boolean; preloaded: boolean } };
  assert.equal(requirements.pluginAssetRelativePath, "/Editor/Plugins/0Harmony.dll");
  assert.deepEqual(requirements.pluginImporterRequirements, { compatibleWithAnyPlatform: false, compatibleWithEditor: true, preloaded: false });

  const meta = await source(join("unitypkg", "Editor", "Plugins", "0Harmony.dll.meta"));
  assert.match(meta, /isExplicitlyReferenced:\s*1/, "auto reference must stay disabled");
  assert.match(meta.slice(meta.indexOf("      Any: ")), /enabled:\s*0/, "Any platform must stay disabled");
  assert.match(meta.slice(meta.indexOf("      Editor: Editor")), /enabled:\s*1/, "Editor platform must stay enabled");

  const hostSource = await source(join("unitypkg", "Editor", "Infrastructure", "MethodHooks", "MethodHookHost.cs"));
  assert.match(hostSource, new RegExp(requirements.pluginAssetRelativePath.replace(/\//g, "\\/")));
  for (const probe of ["GetCompatibleWithAnyPlatform", "GetCompatibleWithEditor", "isPreloaded"]) assert.match(hostSource, new RegExp(probe));
  assert.match(hostSource, /E_HOOK_DEPENDENCY_CONFLICT[\s\S]*unexpected settings/);
});
