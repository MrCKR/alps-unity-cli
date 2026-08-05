import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { staticBridgeToolMetadata } from "../src/tools/catalog.js";
import { builtinToolMetadata } from "../src/tools/metadata/index.js";
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

test("completion contract exactly covers every current public Tool", async () => {
  const expected = [...new Set([...builtinToolMetadata(), ...staticBridgeToolMetadata()].map((tool) => tool.name))].sort();
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
  assert.deepEqual(generated.map(({ completionDriverId: _driver, journalPolicy: _journal, mutation: _mutation, ...entry }) => entry), normalized);
  for (const entry of generated) {
    assert.ok(entry.completionDriverId, entry.toolName);
    assert.ok(["none", "required"].includes(entry.journalPolicy), entry.toolName);
    assert.equal(typeof entry.mutation, "boolean", entry.toolName);
  }
});

test("strict completion audit must pass", () => {
  const result = spawnSync(process.execPath, ["scripts/audit-tool-completion-contract.mjs", "--strict"], {
    cwd: process.cwd(),
    encoding: "utf8",
  });
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
});
