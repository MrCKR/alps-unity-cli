import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "../src/cli.js";

test("public help exposes only the frozen top-level command surface", async () => {
  const result = await execute(["--help"]);
  assert.equal(result.ok, true);
  const data = result.data as { note: string; commands: string[] };
  assert.match(data.note, /Top-level commands are: tools, command, extensions, init, target, instances, doctor\./);
  assert.match(data.note, /CWD is outside the selected Unity project, every `tools` discovery call requires `--instance <projectPath>`/);
  assert.match(data.note, /Tool names returned by group list\/search are exact; groups are filters and are never prepended/);
  assert.equal(data.commands.some(command => command.startsWith("command ") && command.includes("--page-size <n>") && command.includes("--cursor <n>")), true);
  assert.equal(data.commands.some(command => command.startsWith("tools search ") && command.includes("--page-size <n>") && command.includes("--cursor <n>")), true);
  const initCommands = data.commands.filter(command => command.startsWith("init "));
  assert.deepEqual(initCommands, [
    "init help",
    "init discover",
    "init package --project <UnityProject> [--check] [--timeout-ms <n>]",
    "init skill --agent <.agents|claude> --scope workspace --workspace <path> [--check] [--timeout-ms <n>]",
    "init skill --agent <.agents|claude> --scope user [--check] [--timeout-ms <n>]",
  ]);
  assert.match(data.note, /Run `init help` for the self-contained AI installation protocol/);
  assert.doesNotMatch(JSON.stringify(data), /<pi\||\.pi\/skills|--pkg-only|--skill-only/);
  assert.equal(data.commands.some(command => command.startsWith("context ") || command.startsWith("cmd ")), false);
  assert.match(JSON.stringify(data), /--pretty/);
  assert.doesNotMatch(JSON.stringify(data), /--raw/);
  assert.match((result.data as { usage: string }).usage, /^unitycli \[options\] <command> \[options\]$/);
  assert.doesNotMatch(JSON.stringify(data), /\bjobs\b|\bbatch\b|--job\b|--wait\b|wait-timeout-ms|--unity\b|--format\b/);
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
    for (const argv of [["tools", "group", flag], ["doctor", flag]]) {
      const result = await execute(argv);
      assert.equal(result.ok, false);
      assert.equal(result.error.code, "E_OBSOLETE_OPTION");
      assert.match(result.error.message, /has been removed; every Tool call is direct and terminal/);
    }
  }
});

test("removed context top-level command cannot route", async () => {
  const result = await execute(["context", "list"]);
  assert.equal(result.ok, false);
  assert.equal(result.error.code, "UNKNOWN_COMMAND");
});

test("external Unity control-plane operations cannot route as top-level commands", async () => {
  for (const command of ["auth", "hub", "install-unity", "open", "cloud", "license", "proxy"]) {
    const result = await execute([command]);
    assert.equal(result.ok, false);
    assert.equal(result.error.code, "UNKNOWN_COMMAND");
    assert.match(result.error.message, new RegExp(`Unknown command: ${command}`));
  }
});

test("init rejects bare invocation and every removed mixed-install flag", async () => {
  const bare = await execute(["init"]);
  assert.equal(bare.ok, false);
  assert.equal(bare.error.code, "UNKNOWN_INIT_COMMAND");
  for (const argv of [
    ["init", "--project", "Z:/missing-project"],
    ["init", "package", "--project", "Z:/missing-project", "--pkg-only"],
    ["init", "skill", "--agent", ".agents", "--scope", "user", "--skill-only"],
    ["init", "package", "--project", "Z:/missing-project", "--unity", "C:/Unity/Editor/Unity.exe"],
  ]) {
    const result = await execute(argv);
    assert.equal(result.ok, false, argv.join(" "));
    assert.equal(result.error.code, "E_UNKNOWN_OPTION", argv.join(" "));
  }
});
