import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import test from "node:test";

function runAudit(): { ok: boolean; metrics: Record<string, number>; failures: unknown[] } {
  try {
    const stdout = execFileSync(process.execPath, ["scripts/audit-error-codes.mjs"], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
    return JSON.parse(stdout) as { ok: boolean; metrics: Record<string, number>; failures: unknown[] };
  } catch (error) {
    const failure = error as { stdout?: string };
    return JSON.parse(failure.stdout ?? "{\"ok\":false,\"metrics\":{},\"failures\":[{\"rule\":\"audit-crashed\"}]}") as { ok: boolean; metrics: Record<string, number>; failures: unknown[] };
  }
}

test("every public error code is registered, documented and classified", () => {
  const report = runAudit();
  assert.deepEqual(report.failures, [], "error code audit must report no violations");
  assert.ok(report.metrics.registeredCodes > 900);
  assert.equal(report.metrics.registeredCodes, report.metrics.emittedCodes, "registry and source must stay bijective");
  assert.equal(report.metrics.documentedCodes, report.metrics.registeredCodes, "every registered code must appear in public docs");
  assert.equal(report.metrics.unclassifiedCodes, 0, "every code must map to a closed fact");
  assert.equal(report.metrics.renameCandidates, 0, "no code may remain outside the taxonomy");
});

test("the generated registry stays a projection of source, not a hand-edited list", async () => {
  const registry = JSON.parse(await readFile("contracts/error-codes.json", "utf8")) as { generatedBy: string; factVocabulary: string[]; semantics: Record<string, [string, boolean]>; codes: Array<{ code: string; fact: string; effectStarted: string; transient: boolean }> };
  assert.equal(registry.generatedBy, "scripts/gen-error-codes.mjs");
  assert.ok(registry.factVocabulary.length >= 90, "the closed fact vocabulary must stay explicit");
  for (const entry of registry.codes) {
    const semantics = registry.semantics[entry.fact];
    assert.ok(semantics, `${entry.code} must use a closed fact`);
    assert.equal(entry.effectStarted, semantics[0], `${entry.code} effectStarted must come from its fact`);
    assert.equal(entry.transient, semantics[1], `${entry.code} transient must come from its fact`);
  }
});

test("known runtime-concatenated codes stay explicitly enumerated", async () => {
  const dynamic = JSON.parse(await readFile("contracts/error-code-dynamic.json", "utf8")) as { entries: Array<{ file: string; prefix: string; suffixes: string[] }> };
  for (const entry of dynamic.entries) {
    const source = await readFile(entry.file, "utf8");
    for (const suffix of entry.suffixes) assert.ok(source.includes(`errorPrefix + "${suffix}"`), `${entry.file} must still contain ${suffix}`);
  }
  assert.ok(dynamic.entries.length <= 2, "the concatenation exception list must stay small; a new entry needs review");
});
