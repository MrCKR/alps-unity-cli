import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { execute } from "../src/cli.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const runtime = { dataDir: "Z:/definitely-missing-unity-cli-data" } as never;

function withoutRequestId(result: Awaited<ReturnType<typeof execute>>): Omit<typeof result, "requestId"> {
  const { requestId: _requestId, ...stable } = result;
  return stable;
}

test("help and version use the public success envelope", async () => {
  const help = await execute(["--help"], runtime);
  assert.ok(help.ok);
  assert.match(JSON.stringify(help.data), /tools/);
  assert.doesNotMatch(JSON.stringify(help.data), /\b(?:jobs|batch)\b/);

  const version = await execute(["--version"], runtime);
  assert.ok(version.ok);
  assert.match(String((version.data as { version?: string }).version), /^\d+\.\d+\.\d+/);
  assert.deepEqual(Object.keys(version).sort(), ["data", "ok", "requestId"]);
});

test("command help is self-contained before instance discovery", async () => {
  const result = await execute(["command", "--help"], runtime);
  assert.ok(result.ok, JSON.stringify(result));
  assert.match(String((result.data as { usage?: string }).usage), /^unity_cli command/);
});

test("global boolean options do not consume top-level commands", async () => {
  for (const argv of [["--help", "tools", "group"], ["tools", "group", "--help"]]) {
    const result = await execute(argv, runtime);
    assert.equal(result.ok, true);
    assert.match(JSON.stringify(result.data), /tools/);
  }
});

test("arity errors fail before command dispatch", async () => {
  const missing = await execute(["--instance", "tools", "group"], runtime);
  assert.equal(missing.ok, false);
  assert.equal(missing.error.code, "E_OPTION_VALUE_REQUIRED");
  assert.equal((missing.error.details as { option?: string }).option, "--instance");

  const invalid = await execute(["--help=maybe", "tools", "group"], runtime);
  assert.equal(invalid.ok, false);
  assert.equal(invalid.error.code, "E_OPTION_VALUE_INVALID");

  const unknown = await execute(["--internal-route", "tools", "group"], runtime);
  assert.equal(unknown.ok, false);
  assert.equal(unknown.error.code, "E_UNKNOWN_OPTION");
});

test("global value options are equivalent before and after commands", async () => {
  const instanceBefore = await execute(["--instance", "missing-instance", "tools", "group"], runtime);
  const instanceAfter = await execute(["tools", "group", "--instance", "missing-instance"], runtime);
  assert.deepEqual(withoutRequestId(instanceBefore), withoutRequestId(instanceAfter));

  const projectBefore = await execute(["--project", "Z:/missing-project", "extensions", "list"], runtime);
  const projectAfter = await execute(["extensions", "list", "--project", "Z:/missing-project"], runtime);
  assert.deepEqual(withoutRequestId(projectBefore), withoutRequestId(projectAfter));
});

test("generic output format options are rejected", async () => {
  for (const value of ["json", "text", "tsv", "ndjson"]) {
    const result = await execute(["tools", "group", "--format", value], runtime);
    assert.equal(result.ok, false);
    assert.equal(result.error.code, "E_UNKNOWN_OPTION");
  }
});

test("known options are rejected outside their declared route", async () => {
  for (const argv of [
    ["extensions", "list", "--instance", "editor-a"],
    ["instances", "list", "--project", "Z:/missing-project"],
    ["tools", "describe", "demo", "--timeout-ms", "1000"],
  ]) {
    const result = await execute(argv, runtime);
    assert.equal(result.ok, false);
    assert.equal(result.error.code, "E_UNKNOWN_OPTION");
  }
});

test("unknown commands and options return a closed public error envelope", async () => {
  const unknown = await execute(["not-a-command"], runtime);
  assert.ok(!unknown.ok);
  assert.equal(unknown.error?.code, "UNKNOWN_COMMAND");
  assert.deepEqual(Object.keys(unknown).sort(), ["error", "ok", "requestId"]);
  assert.deepEqual(Object.keys(unknown.error ?? {}).sort(), ["code", "message", "nextActions", "retryAfterMs", "transient"]);

  const option = await execute(["tools", "group", "--internal-route", "x"], runtime);
  assert.ok(!option.ok);
  assert.equal(option.error?.code, "E_UNKNOWN_OPTION");

  for (const argv of [
    ["tools", "run", "docs.get", "--fingerprint-mode", "warn"],
    ["tools", "run", "docs.get", "--yes"],
    ["tools", "group", "--include-custom"],
  ]) {
    const fingerprintMode = await execute(argv, runtime);
    assert.ok(!fingerprintMode.ok);
    assert.equal(fingerprintMode.error?.code, "E_UNKNOWN_OPTION");
  }
});

test("compiled CLI emits one raw JSON envelope and a failure exit code", (context) => {
  const cliPath = fileURLToPath(new URL("../src/cli.js", import.meta.url));
  if (!existsSync(cliPath)) {
    context.skip("compiled CLI is verified by build:test output");
    return;
  }
  for (const argv of [["--raw", "not-a-command"], ["not-a-command", "--raw"]]) {
    const result = spawnSync(process.execPath, [cliPath, ...argv], {
      cwd: process.cwd(),
      encoding: "utf8",
      env: { ...process.env, UNITY_CLI_ALLOW_NON_WINDOWS: "1" },
    });
    assert.equal(result.status, 1, result.stderr);
    const lines = result.stdout.trim().split(/\r?\n/);
    assert.equal(lines.length, 1);
    const envelope = JSON.parse(lines[0]) as { ok: boolean; error: { code: string } };
    assert.equal(envelope.ok, false);
    assert.equal(envelope.error.code, "UNKNOWN_COMMAND");
  }
});
