import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "../src/cli.js";

test("public help exposes only the frozen top-level command surface", async () => {
  const result = await execute(["--help"]);
  assert.equal(result.ok, true);
  const data = result.data as { note: string; commands: string[] };
  assert.match(data.note, /Top-level commands are: tools, context, extensions, init, target, instances, doctor\./);
  assert.ok(data.commands.some(command => command.startsWith("context search <keyword>")));
  assert.doesNotMatch(JSON.stringify(data), /\bjobs\b|\bbatch\b|--job\b|--wait\b|wait-timeout-ms|--unity\b/);
});

test("removed jobs and batch top-level commands cannot route", async () => {
  for (const command of ["jobs", "batch"]) {
    const result = await execute([command]);
    assert.equal(result.ok, false);
    assert.equal(result.error.code, "UNKNOWN_COMMAND");
    assert.match(result.error.message, new RegExp(`Unknown command: ${command}`));
  }
});

test("removed deferred execution flags fail before discovery or dispatch", async () => {
  for (const flag of ["--job", "--wait", "--wait-timeout-ms=1000"]) {
    for (const argv of [["tools", "group", flag], ["context", "list", flag], ["doctor", flag]]) {
      const result = await execute(argv);
      assert.equal(result.ok, false);
      assert.equal(result.error.code, "E_OBSOLETE_OPTION");
      assert.match(result.error.message, /has been removed; every Tool call is direct and terminal/);
    }
  }
});

test("init rejects removed Unity executable routing before project access", async () => {
  const result = await execute(["init", "--unity", "C:/Unity/Editor/Unity.exe", "--project", "Z:/missing-project"]);
  assert.equal(result.ok, false);
  assert.equal(result.error.code, "E_OBSOLETE_OPTION");
  assert.match(result.error.message, /init --unity has been removed/);
});
