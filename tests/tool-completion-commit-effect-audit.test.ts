import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";

const root = process.cwd();

test("C# direct Tool commit-effect audit is inventory and descriptor driven", () => {
  const auditPath = path.join(root, "scripts", "audit-tool-completion-contract.mjs");
  const source = readFileSync(auditPath, "utf8");
  assert.match(source, /collectPublicToolInventory/);
  assert.match(source, /csharpDescriptorDeclaresMutation/);
  assert.match(source, /NO_EFFECT_FAIL_CLOSED = new Map/);
  assert.doesNotMatch(source, /Tools\/\*|extensions\/\*/);

  const reportResult = spawnSync(process.execPath, [auditPath], { cwd: root, encoding: "utf8" });
  assert.equal(reportResult.status, 0, reportResult.stderr);
  const report = JSON.parse(reportResult.stdout) as {
    ok: boolean;
    failures: string[];
    csharpCommitEffect: {
      csharpDirectTools: number;
      directMutations: number;
      directReadOnly: number;
      missingCommitEffect: string[];
      readOnlyCommitEffect: string[];
    };
  };
  assert.ok(report.csharpCommitEffect.csharpDirectTools > 0);
  assert.equal(
    report.csharpCommitEffect.directMutations + report.csharpCommitEffect.directReadOnly,
    report.csharpCommitEffect.csharpDirectTools,
  );
  assert.deepEqual(report.csharpCommitEffect.readOnlyCommitEffect, []);
  for (const toolName of report.csharpCommitEffect.missingCommitEffect) {
    assert.ok(report.failures.some((failure) => failure.startsWith(`${toolName}: direct mutation Tool `) && failure.includes("ctx.CommitEffect()")));
  }

  const strictResult = spawnSync(process.execPath, [auditPath, "--strict"], { cwd: root, encoding: "utf8" });
  assert.equal(strictResult.status, report.ok ? 0 : 1, `${strictResult.stdout}\n${strictResult.stderr}`);
});
