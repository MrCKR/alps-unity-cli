import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, unlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import test from "node:test";
import { CLI_OUTPUT_FLUSH_GRACE_MS, execute, finishCliProcess } from "../src/cli.js";
import { serializeResult } from "../src/output.js";

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
  assert.match(JSON.stringify(help.data), /examples\[\]\.input/);
  assert.match(JSON.stringify(help.data), /instances list \[--all\]/);
  assert.match(String((help.data as { note?: string }).note), /pipe-separated tools search keywords in single quotes/);
  assert.doesNotMatch(JSON.stringify(help.data), /examples\[\]\.argv|\b(?:jobs|batch)\b/);

  const version = await execute(["--version"], runtime);
  assert.ok(version.ok);
  assert.match(String((version.data as { version?: string }).version), /^\d+\.\d+\.\d+/);
  assert.deepEqual(Object.keys(version).sort(), ["data", "ok", "requestId"]);
  // help 必须自报版本，调用方无需额外调用 --version 才能判定 CLI 侧版本。
  assert.equal((help.data as { version?: string }).version, (version.data as { version?: string }).version);
});

test("JSON output is compact by default and pretty only when requested", () => {
  const envelope = { ok: true as const, requestId: "req_format", data: { value: 1 } };
  assert.equal(serializeResult(envelope), "{\"ok\":true,\"requestId\":\"req_format\",\"data\":{\"value\":1}}\n");
  const pretty = serializeResult(envelope, { pretty: true });
  assert.match(pretty, /^\{\n  \"ok\": true,/);
  assert.equal(JSON.parse(pretty).data.value, 1);
});

test("command help is self-contained before instance discovery", async () => {
  const result = await execute(["command", "--help"], runtime);
  assert.ok(result.ok, JSON.stringify(result));
  assert.match(String((result.data as { usage?: string }).usage), /^unitycli command/);
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
  assert.equal(unknown.error.code, "E_SHELL_OPTION_UNKNOWN");
});

test("global value options are equivalent before and after commands", async () => {
  const instanceBefore = await execute(["--instance", "missing-instance", "tools", "group"], runtime);
  const instanceAfter = await execute(["tools", "group", "--instance", "missing-instance"], runtime);
  assert.deepEqual(withoutRequestId(instanceBefore), withoutRequestId(instanceAfter));

  const projectBefore = await execute(["--project", "Z:/missing-project", "extensions", "list"], runtime);
  const projectAfter = await execute(["extensions", "list", "--project", "Z:/missing-project"], runtime);
  assert.deepEqual(withoutRequestId(projectBefore), withoutRequestId(projectAfter));
});

test("generic and removed output format options are rejected", async () => {
  for (const value of ["json", "text", "tsv", "ndjson"]) {
    const result = await execute(["tools", "group", "--format", value], runtime);
    assert.equal(result.ok, false);
    assert.equal(result.error.code, "E_SHELL_OPTION_UNKNOWN");
  }
  const removedRaw = await execute(["tools", "group", "--raw"], runtime);
  assert.equal(removedRaw.ok, false);
  assert.equal(removedRaw.error.code, "E_SHELL_OPTION_UNKNOWN");
});

test("known options are rejected outside their declared route", async () => {
  for (const argv of [
    ["extensions", "list", "--instance", "editor-a"],
    ["instances", "list", "--project", "Z:/missing-project"],
    ["tools", "describe", "demo", "--timeout-ms", "1000"],
  ]) {
    const result = await execute(argv, runtime);
    assert.equal(result.ok, false);
    assert.equal(result.error.code, "E_SHELL_OPTION_UNKNOWN");
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
  assert.equal(option.error?.code, "E_SHELL_OPTION_UNKNOWN");

  for (const argv of [
    ["tools", "run", "docs.get", "--fingerprint-mode", "warn"],
    ["tools", "run", "docs.get", "--yes"],
    ["tools", "group", "--include-custom"],
  ]) {
    const fingerprintMode = await execute(argv, runtime);
    assert.ok(!fingerprintMode.ok);
    assert.equal(fingerprintMode.error?.code, "E_SHELL_OPTION_UNKNOWN");
  }
});

test("CLI exit waits for envelope flush and uses one bounded fallback", () => {
  const result = { ok: true as const, requestId: "req_exit_fixture", data: { value: 1 } };
  let output = "";
  let flush: (() => void) | undefined;
  let fallback: (() => void) | undefined;
  let cancelled = 0;
  const exits: number[] = [];

  finishCliProcess(result, false, {
    write: (text, complete) => { output = text; flush = complete; },
    schedule: (complete, delayMs) => {
      assert.equal(delayMs, CLI_OUTPUT_FLUSH_GRACE_MS);
      fallback = complete;
      return "timer";
    },
    cancel: (timer) => { assert.equal(timer, "timer"); cancelled += 1; },
    exit: (code) => { exits.push(code); },
  });

  assert.equal(output, "{\"ok\":true,\"requestId\":\"req_exit_fixture\",\"data\":{\"value\":1}}\n");
  assert.deepEqual(exits, []);
  flush?.();
  assert.deepEqual(exits, [0]);
  assert.equal(cancelled, 1);
  fallback?.();
  assert.deepEqual(exits, [0]);

  let fallbackOnly: (() => void) | undefined;
  const fallbackExits: number[] = [];
  finishCliProcess({ ok: false, requestId: "req_timeout_fixture", error: { code: "E_EXECUTION_TIMEOUT", message: "timeout", nextActions: [], transient: false, retryAfterMs: 0 } }, false, {
    write: () => undefined,
    schedule: (complete) => { fallbackOnly = complete; return "fallback-timer"; },
    cancel: () => undefined,
    exit: (code) => { fallbackExits.push(code); },
  });
  fallbackOnly?.();
  assert.deepEqual(fallbackExits, [1]);
});

test("compiled CLI exits after one envelope even when a preloaded handle remains active", (context) => {
  const cliPath = fileURLToPath(new URL("../src/cli.js", import.meta.url));
  if (!existsSync(cliPath)) {
    context.skip("compiled CLI is verified by build:test output");
    return;
  }
  const preloadPath = join(tmpdir(), `unity-cli-lingering-handle-${process.pid}-${Date.now()}.cjs`);
  writeFileSync(preloadPath, "setInterval(() => {}, 1000);\n", "utf8");
  const startedAt = Date.now();
  try {
    const result = spawnSync(process.execPath, [cliPath, "--help"], {
      cwd: process.cwd(),
      encoding: "utf8",
      timeout: 5_000,
      env: {
        ...process.env,
        NODE_OPTIONS: `${process.env.NODE_OPTIONS ? `${process.env.NODE_OPTIONS} ` : ""}--require=${preloadPath}`,
        UNITY_CLI_ALLOW_NON_WINDOWS: "1",
      },
    });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.signal, null);
    assert.ok(Date.now() - startedAt < 3_000);
    const lines = result.stdout.trim().split(/\r?\n/);
    assert.equal(lines.length, 1);
    assert.equal((JSON.parse(lines[0]) as { ok: boolean }).ok, true);
  } finally {
    unlinkSync(preloadPath);
  }
});

test("compiled CLI emits one envelope and exits within the existing grace with a never-settling local owner", (context) => {
  const cliPath = fileURLToPath(new URL("../src/cli.js", import.meta.url));
  const ownersPath = fileURLToPath(new URL("../src/cli-process-owners.js", import.meta.url));
  if (!existsSync(cliPath) || !existsSync(ownersPath)) {
    context.skip("compiled CLI is verified by build:test output");
    return;
  }
  const script = [
    `import { finishCliProcess } from ${JSON.stringify(pathToFileURL(cliPath).href)};`,
    `import { trackCliProcessOwner } from ${JSON.stringify(pathToFileURL(ownersPath).href)};`,
    "trackCliProcessOwner(new Promise(() => undefined));",
    "finishCliProcess({ ok: true, requestId: 'req_pending_owner', data: { terminal: true } }, false);",
  ].join("\n");
  const startedAt = Date.now();
  const result = spawnSync(process.execPath, ["--input-type=module", "--eval", script], {
    cwd: process.cwd(),
    encoding: "utf8",
    timeout: 4_000,
    env: { ...process.env, UNITY_CLI_ALLOW_NON_WINDOWS: "1" },
  });
  const elapsedMs = Date.now() - startedAt;
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.signal, null);
  assert.ok(elapsedMs >= CLI_OUTPUT_FLUSH_GRACE_MS - 100, `elapsed=${elapsedMs}`);
  assert.ok(elapsedMs < 3_000, `elapsed=${elapsedMs}`);
  const lines = result.stdout.trim().split(/\r?\n/);
  assert.equal(lines.length, 1);
  assert.deepEqual(JSON.parse(lines[0]), { ok: true, requestId: "req_pending_owner", data: { terminal: true } });
});

test("compiled CLI emits one raw JSON envelope and a failure exit code", (context) => {
  const cliPath = fileURLToPath(new URL("../src/cli.js", import.meta.url));
  if (!existsSync(cliPath)) {
    context.skip("compiled CLI is verified by build:test output");
    return;
  }
  for (const argv of [["not-a-command"], ["not-a-command", "--pretty=false"]]) {
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
