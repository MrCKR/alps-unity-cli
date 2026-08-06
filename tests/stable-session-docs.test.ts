import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const root = process.cwd();
const publicDocs = [
  "README.md",
  "docs/target-and-instance.md",
  "docs/tool-execution.md",
  "docs/tool-discovery.md",
  "docs/error-codes.md",
  "docs/availability-differences.md",
  "docs/extensions.md",
  "docs/risk-and-side-effects.md",
  "docs/safety-boundaries.md",
  "skills/unity_cli/SKILL.md",
  "skills/unity_cli/references/custom-tools.md",
  "skills/unity_cli/references/custom-contexts.md",
];

test("stable session public docs contain only current identity and terminal semantics", async () => {
  const documents = await Promise.all(publicDocs.map(async (path) => ({ path, text: await readFile(join(root, path), "utf8") })));
  const forbidden = [
    /tools run editor\.state/,
    /editor\.compile-diagnostics/,
    /waitReady/,
    /target (?:set|current|clear)/,
    /--bridge-endpoint/,
    /E_TARGET_(?:STALE|UNREACHABLE)/,
    /started-before-http-timeout/,
    /tools run[^\n]* --project\b/,
    /tools run[^\n]* --unity\b/,
    /tools run[^\n]* --include-custom\b/,
    /\b(?:logPath|logsRef|jobId|supportsJob|job-deferred)\b/,
    /\b(?:capabilityType|cliCommand|supportStatus|directInvocationPolicy|frozenDeliveryPolicy|extensionAvailability|missingPackageIds)\b/,
    /--unity\b/,
    /\bBridge Command Reference\b/,
    /\b(?:jobs|batch)\b/,
  ];
  for (const document of documents) {
    for (const pattern of forbidden) assert.doesNotMatch(document.text, pattern, `${document.path} still documents removed stable-session behavior`);
  }

  const readme = documents.find((document) => document.path === "README.md")!.text;
  const execution = documents.find((document) => document.path === "docs/tool-execution.md")!.text;
  const target = documents.find((document) => document.path === "docs/target-and-instance.md")!.text;
  assert.match(readme, /context read editor\.state/);
  assert.match(execution, /E_EXECUTION_TIMEOUT/);
  assert.match(execution, /compile\.completed=true/);
  assert.match(target, /CanonicalPath v1/);
  assert.match(target, /validated generation/);
});
