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

test("init help is self-contained for minimal decisions, explicit targets, open-Editor installs, and independent effects", async () => {
  const result = await execute(["init", "help"]);
  assert.equal(result.ok, true, JSON.stringify(result));
  if (!result.ok) throw new Error("Expected init help success.");
  const data = result.data as any;
  assert.equal(data.action, "init_help");
  assert.equal(data.audience, "ai");
  assert.equal(data.protocolVersion, 3);
  assert.match(data.version, /^\d+\.\d+\.\d+/);
  assert.equal(data.nextCommand, "unitycli init discover");
  assert.deepEqual(Object.keys(data.commands).sort(), ["discover", "explicitSkillCheck", "explicitSkillInstall", "packageCheck", "packageInstall", "packageInstallAndCloseEditor", "userSkillCheck", "userSkillInstall", "workspaceSkillCheck", "workspaceSkillInstall"]);
  assert.match(data.commands.packageInstall, /--project <UnityProject>$/);
  assert.match(data.commands.packageInstallAndCloseEditor, /--close-editor/);
  assert.equal("packageInstallHot" in data.commands, false);
  assert.match(data.commands.explicitSkillInstall, /--target <SkillDirectory>/);
  assert.match(data.commands.workspaceSkillInstall, /--agent <\.agents\|claude> --scope workspace --workspace <path>/);
  const instructions = data.aiInstructions.join("\n");
  assert.match(instructions, /Run `unitycli init discover`/);
  assert.match(instructions, /user already specified as final/i);
  assert.match(instructions, /Ask only entries present in discovery.requiredDecisions/i);
  assert.match(instructions, /open Unity Editor is a normal installation state/i);
  assert.match(instructions, /never closes, starts, or restarts Unity for you/i);
  assert.match(instructions, /PACKAGE_DIRECTORY_LOCKED/);
  assert.match(instructions, /ask once whether this exact Unity project may be closed normally/i);
  assert.match(instructions, /never force-terminate/i);
  assert.match(instructions, /tools run editor\.refresh/);
  assert.match(instructions, /`changed:false` means the Editor had nothing left to import/i);
  assert.match(instructions, /`cli\.version`, `cli\.compatibilityRange`/);
  assert.match(instructions, /independent authorized effects/i);
  assert.match(instructions, /failure blocks only that effect/i);
  assert.match(instructions, /corresponding `--check`/);
  assert.match(instructions, /PACKAGE_ASSEMBLY_CONFLICT/);
  assert.match(instructions, /PACKAGE_ASSEMBLY_SCAN_BUDGET_EXHAUSTED/);
  assert.match(instructions, /never delete, move, or rename project files yourself/i);
  assert.match(instructions, /let the user decide which copy to remove or relocate/i);
  assert.doesNotMatch(instructions, /--hot|Stop immediately after the first unexpected|do not continue with later installation/i);
  assert.doesNotMatch(JSON.stringify(data), /README|pkg-only|skill-only/);
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
      assert.ok(["E_INIT_ARGUMENT_UNEXPECTED", "E_SHELL_OPTION_UNKNOWN"].includes(invalid.error.code));
    }
    assert.deepEqual(await tree(root), before);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
