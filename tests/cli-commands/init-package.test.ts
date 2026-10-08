import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../../src/cli.js";
import { hashPackageTree } from "../../src/package-manager.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeUnityProject(): Promise<string> {
  const project = await mkdtemp(join(tmpdir(), "unity-init-package-"));
  await mkdir(join(project, "Assets"), { recursive: true });
  await mkdir(join(project, "Packages"), { recursive: true });
  await mkdir(join(project, "ProjectSettings"), { recursive: true });
  await writeFile(join(project, "Packages", "manifest.json"), "{\n  \"dependencies\": {}\n}\n", "utf8");
  await writeFile(join(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.61f1\n", "utf8");
  return project;
}

function runtime(project: string, overrides: Record<string, unknown> = {}): Record<string, unknown> { return { dataDir: join(project, "data"), findUnityProjectProcesses: async () => [], ...overrides }; }

function runningEditor(project: string) {
  return { processId: 42, processStartedAt: "2026-08-31T00:00:00.000Z", executablePath: "C:\\Unity\\Unity.exe", argv: ["Unity.exe", "-projectPath", project], projectPath: project };
}

test("init routes only help, discover, package, and skill subcommands", async () => {
  const userHome = await mkdtemp(join(tmpdir(), "unity-init-user-home-"));
  try {
    const help = await execute(["init", "help"], { dataDir: join(userHome, "data"), userHome } as never);
    assert.equal(help.ok, true, JSON.stringify(help));
    assert.equal((help.data as any).action, "init_help");

    const discover = await execute(["init", "discover"], { dataDir: join(userHome, "data"), userHome } as never);
    assert.equal(discover.ok, true, JSON.stringify(discover));
    assert.equal((discover.data as any).action, "init_discover");

    const skill = await execute(["init", "skill", "--agent", ".agents", "--scope", "user", "--check"], { dataDir: join(userHome, "data"), userHome } as never);
    assert.equal(skill.ok, true, JSON.stringify(skill));
    assert.equal((skill.data as any).action, "init_skill_check");
    assert.equal((skill.data as any).skill.status, "missing");

    const unknown = await execute(["init", "setup"], { dataDir: join(userHome, "data"), userHome } as never);
    assert.equal(unknown.ok, false);
    assert.equal(unknown.error.code, "UNKNOWN_INIT_COMMAND");
  } finally {
    await rm(userHome, { recursive: true, force: true });
  }
});

test("init package installs, checks read-only, and repairs managed damage", async () => {
  const project = await makeUnityProject();
  try {
    const installed = await execute(["init", "package", "--project", project], runtime(project) as never);
    assert.equal(installed.ok, true, JSON.stringify(installed));
    if (!installed.ok) throw new Error("Expected package installation.");
    assert.equal((installed.data as any).action, "init_package");
    assert.equal((installed.data as any).package.status, "installed");
    const bridgePath = (installed.data as any).package.install.bridgePath as string;
    const beforeCheck = await hashPackageTree(bridgePath);

    const checked = await execute(["init", "package", "--project", project, "--check"], runtime(project) as never);
    assert.equal(checked.ok, true, JSON.stringify(checked));
    assert.equal((checked.data as any).package.status, "installed");
    assert.equal(await hashPackageTree(bridgePath), beforeCheck);

    const managedFile = join(bridgePath, "Editor", "UnityCliBridge.cs");
    await writeFile(managedFile, "// damaged\n", "utf8");
    const damaged = await execute(["init", "package", "--project", project, "--check"], runtime(project) as never);
    assert.equal(damaged.ok, true, JSON.stringify(damaged));
    assert.equal((damaged.data as any).package.status, "damaged");
    assert.equal((damaged.data as any).package.reasonCode, "PACKAGE_INSTALL_DAMAGED");

    const repaired = await execute(["init", "package", "--project", project], runtime(project) as never);
    assert.equal(repaired.ok, true, JSON.stringify(repaired));
    assert.equal((repaired.data as any).package.readback.status, "installed");
    assert.match(await readFile(managedFile, "utf8"), /class UnityCliBridge/);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("init package requires explicit close authorization for a running exact Editor", async () => {
  const project = await makeUnityProject();
  try {
    let closeCalls = 0;
    const processRuntime = runtime(project, {
      findUnityProjectProcesses: async () => [runningEditor(project)],
      closeUnityProjectProcesses: async () => { closeCalls += 1; return { requested: 1, remaining: 0 }; },
    }) as never;
    const blocked = await execute(["init", "package", "--project", project], processRuntime);
    assert.equal(blocked.ok, false, JSON.stringify(blocked));
    assert.equal(blocked.error.code, "E_INIT_EDITOR_RUNNING");
    assert.equal(closeCalls, 0);
    await assert.rejects(() => readFile(join(project, "Packages", "com.alpsckr.unitycli", "package.json")), { code: "ENOENT" });

    const installed = await execute(["init", "package", "--project", project, "--close-editor"], processRuntime);
    assert.equal(installed.ok, true, JSON.stringify(installed));
    assert.equal(closeCalls, 1);
    assert.deepEqual((installed as any).data.editorClose, { status: "closed", requested: 1 });
    assert.equal((installed as any).data.package.readback.status, "installed");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("init package fails closed when exact Editor state cannot be verified", async () => {
  const project = await makeUnityProject();
  try {
    const result = await execute(["init", "package", "--project", project], runtime(project, {
      findUnityProjectProcesses: async () => { throw new Error("process observation denied"); },
    }) as never);
    assert.equal(result.ok, false, JSON.stringify(result));
    assert.equal(result.error.code, "E_EDITOR_STATE_UNVERIFIABLE");
    await assert.rejects(() => readFile(join(project, "Packages", "com.alpsckr.unitycli", "package.json")), { code: "ENOENT" });
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("init package stops without package effects when normal Editor close remains blocked", async () => {
  const project = await makeUnityProject();
  try {
    const result = await execute(["init", "package", "--project", project, "--close-editor"], runtime(project, {
      findUnityProjectProcesses: async () => [runningEditor(project)],
      closeUnityProjectProcesses: async () => ({ requested: 1, remaining: 1 }),
    }) as never);
    assert.equal(result.ok, false, JSON.stringify(result));
    assert.equal(result.error.code, "E_EDITOR_CLOSE_BLOCKED");
    assert.match(result.error.nextActions[0], /unsaved-scene|modal/i);
    await assert.rejects(() => readFile(join(project, "Packages", "com.alpsckr.unitycli", "package.json")), { code: "ENOENT" });
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("init package rejects missing project, positional arguments, old flags, and Skill options", async () => {
  const project = await makeUnityProject();
  try {
    const cases: Array<{ argv: string[]; code: string }> = [
      { argv: ["init", "package"], code: "E_INIT_PROJECT_REQUIRED" },
      { argv: ["init", "package", "extra", "--project", project], code: "E_INIT_ARGUMENT_UNEXPECTED" },
      { argv: ["init", "package", "--project", project, "--pkg-only"], code: "E_UNKNOWN_OPTION" },
      { argv: ["init", "package", "--project", project, "--agent", ".agents"], code: "E_UNKNOWN_OPTION" },
      { argv: ["init", "package", "--project", project, "--timeout-ms", "0"], code: "E_INIT_TIMEOUT_INVALID" },
      { argv: ["init", "package", "--project", project, "--check", "--close-editor"], code: "E_INIT_CLOSE_EDITOR_CHECK_FORBIDDEN" },
    ];
    for (const item of cases) {
      const result = await execute(item.argv, runtime(project) as never);
      assert.equal(result.ok, false, JSON.stringify(result));
      if (!result.ok) assert.equal(result.error.code, item.code, item.argv.join(" "));
    }
    await assert.rejects(() => readFile(join(project, "Packages", "com.alpsckr.unitycli", "package.json")), { code: "ENOENT" });
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("init package exposes structured assembly conflicts before target effects", async () => {
  const project = await makeUnityProject();
  try {
    const conflict = join(project, "Packages", "com.example.conflict", "Plugins", "0Harmony.dll");
    await mkdir(join(conflict, ".."), { recursive: true });
    await writeFile(conflict, "conflict", "utf8");
    const result = await execute(["init", "package", "--project", project], runtime(project) as never);
    assert.equal(result.ok, false, JSON.stringify(result));
    if (!result.ok) {
      assert.equal(result.error.code, "PACKAGE_ASSEMBLY_CONFLICT");
      const details = result.error.details as { conflicts?: Array<{ provider?: string; relativePath?: string; sha256?: string }> };
      assert.equal(details.conflicts?.[0]?.provider, "com.example.conflict");
      assert.match(String(details.conflicts?.[0]?.relativePath), /com\.example\.conflict/);
      assert.match(String(details.conflicts?.[0]?.sha256), /^[a-f0-9]{64}$/);
      const guidance = result.error.nextActions.join("\n");
      assert.match(guidance, /identicalToPackageDependency/);
      assert.match(guidance, /never delete, move, or rename project files yourself/i);
      assert.match(guidance, /nothing was installed/i);
    }
    await assert.rejects(() => readFile(join(project, "Packages", "com.alpsckr.unitycli", "package.json")), { code: "ENOENT" });

    // 只读检查路径必须给出与安装路径同一份冲突处理指引，不能只说“有冲突”。
    const checked = await execute(["init", "package", "--project", project, "--check"], runtime(project) as never);
    assert.equal(checked.ok, true, JSON.stringify(checked));
    if (checked.ok) {
      const data = checked.data as { package?: { status?: string }; nextActions?: string[] };
      assert.equal(data.package?.status, "conflict");
      const checkGuidance = (data.nextActions ?? []).join("\n");
      assert.match(checkGuidance, /identicalToPackageDependency/);
      assert.match(checkGuidance, /never delete, move, or rename project files yourself/i);
    }
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});
