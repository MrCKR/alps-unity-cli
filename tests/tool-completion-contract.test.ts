import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import test from "node:test";
import { GENERATED_TOOL_COMPLETION_CONTRACTS } from "../src/tools/.generated-completion-registry.js";

interface CompletionFacet { kind: string; description: string; }
interface CompletionEntry {
  toolName: string;
  deliveryMode: string;
  executionClass: string;
  completionPredicate: CompletionFacet;
  proofSource: CompletionFacet;
  reloadPolicy: CompletionFacet;
  timeoutError: string;
  outcomeUnknownPolicy: CompletionFacet;
  descriptorOwner: string;
}

async function contractEntries(): Promise<CompletionEntry[]> {
  const contract = JSON.parse(await readFile(new URL("../contracts/tool-completion-contract.json", import.meta.url), "utf8")) as { tools: CompletionEntry[] };
  return contract.tools;
}

test("completion contract exactly covers every canonical public Tool", async () => {
  const moduleUrl = pathToFileURL(join(process.cwd(), "scripts", "lib", "public-tool-inventory.mjs")).href;
  const inventoryModule = await import(moduleUrl) as { collectPublicToolInventory(root: string): Array<{ name: string }> };
  const expected = inventoryModule.collectPublicToolInventory(process.cwd()).map((tool) => tool.name).sort();
  const entries = await contractEntries();
  const actual = entries.map((entry) => entry.toolName).sort();
  assert.deepEqual(actual, expected);
  assert.equal(new Set(actual).size, actual.length);
  assert.equal(actual.includes("build.status"), false);
  assert.equal(actual.includes("build.cancel"), false);
});

test("all completion contracts are direct terminal and executable", async () => {
  const entries = await contractEntries();
  for (const entry of entries) {
    assert.equal(entry.deliveryMode, "direct-terminal", entry.toolName);
    assert.ok(["atomic", "observed-state", "resumable", "external"].includes(entry.executionClass), entry.toolName);
    for (const field of ["completionPredicate", "proofSource", "reloadPolicy", "outcomeUnknownPolicy"] as const) {
      assert.ok(entry[field]?.kind, `${entry.toolName}.${field}.kind`);
      assert.ok(entry[field]?.description, `${entry.toolName}.${field}.description`);
    }
    assert.ok(entry.timeoutError, `${entry.toolName}.timeoutError`);
    assert.ok(entry.descriptorOwner, `${entry.toolName}.descriptorOwner`);
    assert.doesNotMatch(`${entry.completionPredicate.kind}\n${entry.proofSource.kind}`, /handler[- ]return|accepted|queued|requested|fixed true/i, entry.toolName);
  }
});

test("generated TypeScript completion registry is exact contract material", async () => {
  const entries = await contractEntries();
  const normalized = entries.map((entry) => ({
    toolName: entry.toolName,
    deliveryMode: entry.deliveryMode,
    executionClass: entry.executionClass,
    completionPredicate: entry.completionPredicate.kind,
    proofSource: entry.proofSource.kind,
    reloadPolicy: entry.reloadPolicy.kind,
    timeoutError: entry.timeoutError,
    outcomeUnknownPolicy: entry.outcomeUnknownPolicy.kind,
    descriptorOwner: entry.descriptorOwner,
  })).sort((left, right) => left.toolName.localeCompare(right.toolName));
  const generated = [...GENERATED_TOOL_COMPLETION_CONTRACTS].sort((left, right) => left.toolName.localeCompare(right.toolName));
  assert.deepEqual(generated.map(({ completionDriverId: _driver, journalPolicy: _journal, mutation: _mutation, assemblyName: _assembly, implementationType: _type, ...entry }) => entry), normalized);
  for (const entry of generated) {
    assert.ok(entry.completionDriverId, entry.toolName);
    assert.ok(["none", "required"].includes(entry.journalPolicy), entry.toolName);
    assert.equal(typeof entry.mutation, "boolean", entry.toolName);
    if (entry.descriptorOwner === "built-in" || entry.descriptorOwner.startsWith("extension:")) {
      assert.ok(entry.assemblyName, `${entry.toolName}.assemblyName`);
      assert.ok(entry.implementationType, `${entry.toolName}.implementationType`);
    } else {
      assert.equal(entry.assemblyName, undefined, `${entry.toolName}.assemblyName`);
      assert.equal(entry.implementationType, undefined, `${entry.toolName}.implementationType`);
    }
  }
});

test("strict completion audit must pass", () => {
  const result = spawnSync(process.execPath, ["scripts/audit-tool-completion-contract.mjs", "--strict"], {
    cwd: process.cwd(),
    encoding: "utf8",
  });
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
});

// TS 运行时把 Tool 名称直接当作 bridge command 发送时，Bridge 只能按名称解析完成契约；
// 这些名称漏进生成表会让公开 Tool 永远被准入拒绝，所以从真实发送者推导并逐一核对。
test("every bridge command the TS runtime dispatches by Tool name resolves a C# completion contract", async () => {
  const generated = await readFile(join(process.cwd(), "unitypkg", "Editor", "Generated", "UnityCliToolCompletionRegistry.g.cs"), "utf8");
  const generatedNames = new Set([...generated.matchAll(/new UnityCliToolCompletionContract\("([^"]+)"/g)].map((match) => match[1]));
  const toolNames = new Set(GENERATED_TOOL_COMPLETION_CONTRACTS.map((entry) => entry.toolName));
  const dispatched = new Set<string>();
  for (const source of ["src/runtime-code.ts", "src/runtime-flow.ts"]) {
    const text = await readFile(join(process.cwd(), source), "utf8");
    for (const match of text.matchAll(/"([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)"/g)) if (toolNames.has(match[1])) dispatched.add(match[1]);
  }
  assert.ok(dispatched.size > 0, "no self-dispatched bridge command was derived from the TS runtime sources");
  assert.deepEqual([...dispatched].filter((name) => !generatedNames.has(name)).sort(), []);
});
