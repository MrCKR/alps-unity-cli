import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../../src/cli.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";
async function makeUnityProject(root: string): Promise<string> {
  const project = join(root, "UnityProject");
  await mkdir(join(project, "Assets"), { recursive: true });
  await mkdir(join(project, "Packages"), { recursive: true });
  await mkdir(join(project, "ProjectSettings"), { recursive: true });
  await writeFile(join(project, "Packages", "manifest.json"), "{\"dependencies\":{}}\n", "utf8");
  await writeFile(join(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.61f1\n", "utf8");
  return project;
}
async function files(root: string, relative = ""): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(join(root, relative), { withFileTypes: true })) {
    const child = relative ? join(relative, entry.name) : entry.name;
    if (entry.isDirectory()) result.push(...await files(root, child)); else result.push(child.replaceAll("\\", "/"));
  }
  return result.sort();
}

test("final init success and decision states share explicit actions and nextActions", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-init-protocol-"));
  try {
    const project = await makeUnityProject(root);
    const runtime = { dataDir: join(root, "data"), userHome: root } as never;
    const help = await execute(["init", "help"], runtime);
    const discover = await execute(["init", "discover"], runtime);
    const packageCheck = await execute(["init", "package", "--project", project, "--check"], runtime);
    const skillCheck = await execute(["init", "skill", "--agent", ".agents", "--scope", "user", "--check"], runtime);
    for (const result of [help, discover, packageCheck, skillCheck]) {
      assert.equal(result.ok, true, JSON.stringify(result));
      assert.equal(typeof (result.data as any).action, "string");
      assert.ok(Array.isArray((result.data as any).nextActions));
    }
    const discoverData = (discover as { ok: true; data: any }).data;
    const packageData = (packageCheck as { ok: true; data: any }).data;
    const skillData = (skillCheck as { ok: true; data: any }).data;
    assert.ok(Array.isArray(discoverData.requiredDecisions));
    assert.equal(packageData.package.status, "missing");
    assert.ok(packageData.nextActions.length > 0);
    assert.equal(skillData.skill.status, "missing");
    assert.ok(skillData.nextActions[0].includes("init skill"));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("final init invalid inputs fail before effects with stable closed errors", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-init-errors-"));
  try {
    const project = await makeUnityProject(root);
    const runtime = { dataDir: join(root, "data"), userHome: root } as never;
    const before = await files(root);
    const cases: Array<{ argv: string[]; code: string }> = [
      { argv: ["init"], code: "UNKNOWN_INIT_COMMAND" },
      { argv: ["init", "unknown"], code: "UNKNOWN_INIT_COMMAND" },
      { argv: ["init", "package"], code: "E_INIT_PROJECT_REQUIRED" },
      { argv: ["init", "package", "--project", project, "extra"], code: "E_INIT_ARGUMENT_UNEXPECTED" },
      { argv: ["init", "skill", "--scope", "user"], code: "E_INIT_AGENT_REQUIRED" },
      { argv: ["init", "skill", "--agent", "pi", "--scope", "user"], code: "UNKNOWN_AGENT_KIND" },
      { argv: ["init", "skill", "--agent", "claude,.agents", "--scope", "user"], code: "E_INIT_AGENT_INVALID" },
      { argv: ["init", "skill", "--agent", ".agents"], code: "E_INIT_SCOPE_INVALID" },
      { argv: ["init", "skill", "--agent", ".agents", "--scope", "workspace"], code: "E_INIT_WORKSPACE_REQUIRED" },
      { argv: ["init", "skill", "--agent", ".agents", "--scope", "user", "--workspace", root], code: "E_INIT_WORKSPACE_FORBIDDEN" },
      { argv: ["init", "help", "--check"], code: "E_UNKNOWN_OPTION" },
      { argv: ["init", "discover", "--project", project], code: "E_UNKNOWN_OPTION" },
    ];
    for (const item of cases) {
      const result = await execute(item.argv, runtime);
      assert.equal(result.ok, false, item.argv.join(" "));
      assert.equal(result.error.code, item.code, item.argv.join(" "));
      assert.equal(typeof result.error.message, "string");
      assert.ok(Array.isArray(result.error.nextActions));
      assert.equal(result.error.transient, false);
      assert.equal(result.error.retryAfterMs, 0);
      assert.ok(Object.keys(result.error).every((key) => ["code", "message", "nextActions", "transient", "retryAfterMs", "details"].includes(key)));
    }
    assert.deepEqual(await files(root), before);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("init protocol remains non-interactive and does not persist setup choices", async () => {
  const sources = await Promise.all([
    readFile("src/cli-commands/package.ts", "utf8"),
    readFile("src/cli-commands/init-help.ts", "utf8"),
    readFile("src/cli-commands/init-discovery.ts", "utf8"),
    readFile("src/cli-commands/init-skill.ts", "utf8"),
  ]);
  const text = sources.join("\n");
  assert.doesNotMatch(text, /readline|inquirer|prompt\(|question\(|confirm\(|settings\.json|config\.json/i);
  assert.doesNotMatch(text, /pkg-only|skill-only|defaultAgentKinds|detectAgentKinds/);
});
