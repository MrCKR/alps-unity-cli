import assert from "node:assert/strict";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../../src/cli.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function tree(root: string, relative = ""): Promise<Array<{ path: string; content: string }>> {
  const result: Array<{ path: string; content: string }> = [];
  for (const entry of await readdir(join(root, relative), { withFileTypes: true })) {
    const child = relative ? join(relative, entry.name) : entry.name;
    if (entry.isDirectory()) result.push(...await tree(root, child));
    else if (entry.isFile()) result.push({ path: child.replaceAll("\\", "/"), content: await readFile(join(root, child), "utf8") });
  }
  return result.sort((left, right) => left.path.localeCompare(right.path));
}

test("init help is self-contained for AI decisions, serial effects, checks, and stop boundaries", async () => {
  const result = await execute(["init", "help"]);
  assert.equal(result.ok, true, JSON.stringify(result));
  if (!result.ok) throw new Error("Expected init help success.");
  const data = result.data as any;
  assert.equal(data.action, "init_help");
  assert.equal(data.audience, "ai");
  assert.equal(data.protocolVersion, 1);
  assert.equal(data.nextCommand, "unitycli init discover");
  assert.deepEqual(Object.keys(data.commands).sort(), ["discover", "packageCheck", "packageInstall", "userSkillCheck", "userSkillInstall", "workspaceSkillCheck", "workspaceSkillInstall"]);
  assert.match(data.commands.packageInstall, /init package --project <UnityProject>/);
  assert.match(data.commands.workspaceSkillInstall, /--agent <\.agents\|claude> --scope workspace --workspace <path>/);
  assert.match(data.commands.userSkillInstall, /--scope user/);
  const instructions = data.aiInstructions.join("\n");
  assert.match(instructions, /Run `unitycli init discover`/);
  assert.match(instructions, /ask the user.*Unity projects.*agent\/scope\/workspace/i);
  assert.match(instructions, /strict serial order/);
  assert.match(instructions, /Skill only once/i);
  assert.match(instructions, /corresponding command with `--check`/);
  assert.match(instructions, /Stop immediately after the first unexpected CLI failure or failed business check/);
  assert.match(instructions, /Do not start, close, restart, or terminate Unity Editor/);
  assert.match(instructions, /authorized effects.*do not add another confirmation gate/i);
  assert.doesNotMatch(JSON.stringify(data), /README|pkg-only|skill-only|\.pi\/skills/);
});

test("init help reads no workspace files and rejects additional input", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-init-help-readonly-"));
  try {
    await writeFile(join(root, "sentinel.txt"), "unchanged\n", "utf8");
    const before = await tree(root);
    const result = await execute(["init", "help"], { dataDir: join(root, "data") } as never);
    assert.equal(result.ok, true, JSON.stringify(result));
    assert.deepEqual(await tree(root), before);

    for (const argv of [["init", "help", "extra"], ["init", "help", "--project", root], ["init", "help", "--check"]]) {
      const invalid = await execute(argv, { dataDir: join(root, "data") } as never);
      assert.equal(invalid.ok, false, argv.join(" "));
      assert.ok(["E_INIT_ARGUMENT_UNEXPECTED", "E_UNKNOWN_OPTION"].includes(invalid.error.code));
    }
    assert.deepEqual(await tree(root), before);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
