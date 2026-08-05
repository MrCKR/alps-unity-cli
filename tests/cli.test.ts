import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { execute } from "../src/cli.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const runtime = { dataDir: "Z:/definitely-missing-unity-cli-data" } as never;

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
    ["tools", "run", "docs.list", "--fingerprint-mode", "warn"],
    ["tools", "run", "docs.list", "--yes"],
    ["context", "describe", "editor.state", "--fingerprint-mode", "block"],
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
  const result = spawnSync(process.execPath, [cliPath, "not-a-command", "--raw"], {
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
});
