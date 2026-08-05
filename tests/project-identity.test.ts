import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import {
  CANONICALIZATION_VERSION,
  clearProjectIdentityCache,
  ProjectIdentityError,
  resolveProjectIdentity,
  resolveProjectIdentityAsync,
  type AsyncNativeCanonicalProjectPathResolver,
  type NativeCanonicalProjectPathResolver,
} from "../src/project-identity.js";

/** 创建最小 Unity project root fixture。 */
async function createUnityProject(root: string): Promise<void> {
  await Promise.all([
    mkdir(path.join(root, "Assets", "Scripts"), { recursive: true }),
    mkdir(path.join(root, "Packages"), { recursive: true }),
    mkdir(path.join(root, "ProjectSettings"), { recursive: true }),
  ]);
  await writeFile(path.join(root, "Packages", "manifest.json"), "{}\n", "utf8");
  await writeFile(path.join(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f1\n", "utf8");
}

test("CanonicalPath v1 对 root、深层目录、尾分隔符与大小写返回同一 identity", async () => {
  const base = await mkdtemp(path.join(tmpdir(), "unity-cli-identity-"));
  const project = path.join(base, "MixedCaseProject");
  await createUnityProject(project);
  try {
    clearProjectIdentityCache();
    const rootIdentity = resolveProjectIdentity(project);
    const childIdentity = resolveProjectIdentity(path.join(project, "Assets", "Scripts", "..", "Scripts"));
    const trailingIdentity = resolveProjectIdentity(`${project}${path.sep}`);
    assert.equal(rootIdentity.canonicalizationVersion, CANONICALIZATION_VERSION);
    assert.equal(childIdentity.instanceId, rootIdentity.instanceId);
    assert.equal(trailingIdentity.instanceId, rootIdentity.instanceId);
    assert.match(rootIdentity.instanceId, /^unity-[0-9a-f]{64}$/);
    assert.equal(rootIdentity.canonicalProjectPath, rootIdentity.canonicalProjectPath.toLowerCase());
  } finally {
    await rm(base, { recursive: true, force: true });
    clearProjectIdentityCache();
  }
});

test("CanonicalPath v1 的同步与异步 helper 保留非 ASCII project path", async () => {
  const base = await mkdtemp(path.join(tmpdir(), "unity-cli-identity-unicode-"));
  const project = path.join(base, "中文项目");
  await createUnityProject(project);
  try {
    clearProjectIdentityCache();
    const syncIdentity = resolveProjectIdentity(project);
    clearProjectIdentityCache();
    const asyncIdentity = await resolveProjectIdentityAsync(project, { timeoutMs: 15_000 });
    assert.equal(asyncIdentity.instanceId, syncIdentity.instanceId);
    assert.equal(asyncIdentity.canonicalProjectPath, syncIdentity.canonicalProjectPath);
    assert.match(syncIdentity.displayProjectPath, /中文项目/);
    assert.doesNotMatch(syncIdentity.displayProjectPath, /�/);
  } finally {
    await rm(base, { recursive: true, force: true });
    clearProjectIdentityCache();
  }
});

test("CanonicalPath v1 先解析 junction 与 directory symlink 再发现项目 root", async () => {
  const base = await mkdtemp(path.join(tmpdir(), "unity-cli-identity-reparse-"));
  const project = path.join(base, "PhysicalProject");
  const junction = path.join(base, "JunctionProject");
  const directoryLink = path.join(base, "SymlinkProject");
  await createUnityProject(project);
  await symlink(project, junction, "junction");
  await symlink(project, directoryLink, "dir");
  try {
    clearProjectIdentityCache();
    const physical = resolveProjectIdentity(project);
    for (const linkedPath of [junction, directoryLink]) {
      const linked = resolveProjectIdentity(path.join(linkedPath, "Assets"));
      assert.equal(linked.instanceId, physical.instanceId);
      assert.equal(linked.canonicalProjectPath, physical.canonicalProjectPath);
    }
  } finally {
    await rm(base, { recursive: true, force: true });
    clearProjectIdentityCache();
  }
});

test("CanonicalPath v1 将本机 SUBST 归约到同一物理 identity", async () => {
  const base = await mkdtemp(path.join(tmpdir(), "unity-cli-identity-subst-"));
  const project = path.join(base, "SubstProject");
  await createUnityProject(project);
  let drive = "";
  try {
    for (let code = "Z".charCodeAt(0); code >= "T".charCodeAt(0); code -= 1) {
      const candidate = `${String.fromCharCode(code)}:`;
      if (existsSync(`${candidate}\\`)) continue;
      try {
        execFileSync("subst.exe", [candidate, project], { stdio: "pipe", windowsHide: true });
        drive = candidate;
        break;
      } catch {
        // 继续尝试下一空闲盘符，最终无可用盘符时测试失败。
      }
    }
    assert.notEqual(drive, "", "No free local drive letter was available for SUBST validation.");
    clearProjectIdentityCache();
    const physical = resolveProjectIdentity(project);
    const substituted = resolveProjectIdentity(`${drive}\\Assets`);
    assert.equal(substituted.instanceId, physical.instanceId);
    assert.equal(substituted.canonicalProjectPath, physical.canonicalProjectPath);
  } finally {
    if (drive) execFileSync("subst.exe", [drive, "/d"], { stdio: "pipe", windowsHide: true });
    await rm(base, { recursive: true, force: true });
    clearProjectIdentityCache();
  }
});

test("CanonicalPath v1 对 case-sensitive directory fail closed 或证明宿主无法构造 fixture", async () => {
  const base = await mkdtemp(path.join(tmpdir(), "unity-cli-identity-case-sensitive-"));
  let enabled = false;
  try {
    try {
      execFileSync("fsutil.exe", ["file", "SetCaseSensitiveInfo", base, "enable"], { stdio: "pipe", windowsHide: true });
      enabled = true;
    } catch {
      const query = new TextDecoder("gbk").decode(execFileSync("fsutil.exe", ["file", "QueryCaseSensitiveInfo", base], { windowsHide: true }));
      assert.match(query, /disabled|已禁用/i);
      const source = await readFile("unitypkg/Editor/Infrastructure/UnityCliCanonicalProjectPath.cs", "utf8");
      assert.match(source, /FileCaseSensitiveInfo/);
      assert.match(source, /E_INSTANCE_PATH_CASE_SENSITIVE_UNSUPPORTED/);
      return;
    }
    const project = path.join(base, "CaseSensitiveProject");
    await createUnityProject(project);
    clearProjectIdentityCache();
    assert.throws(
      () => resolveProjectIdentity(project),
      (error: unknown) => error instanceof ProjectIdentityError && error.code === "E_INSTANCE_PATH_CASE_SENSITIVE_UNSUPPORTED",
    );
  } finally {
    if (enabled) execFileSync("fsutil.exe", ["file", "SetCaseSensitiveInfo", base, "disable"], { stdio: "pipe", windowsHide: true });
    await rm(base, { recursive: true, force: true });
    clearProjectIdentityCache();
  }
});

test("CanonicalPath v1 对 broken directory symlink fail closed", async () => {
  const base = await mkdtemp(path.join(tmpdir(), "unity-cli-identity-broken-"));
  const broken = path.join(base, "BrokenProject");
  await symlink(path.join(base, "MissingTarget"), broken, "dir");
  try {
    clearProjectIdentityCache();
    assert.throws(
      () => resolveProjectIdentity(path.join(broken, "Assets")),
      (error: unknown) => error instanceof ProjectIdentityError && error.code === "E_INSTANCE_PATH_UNAVAILABLE",
    );
  } finally {
    await rm(base, { recursive: true, force: true });
    clearProjectIdentityCache();
  }
});

test("CanonicalPath v1 静态 identity hash golden 保持逐字节稳定", async () => {
  const fixture = JSON.parse(await readFile("contracts/stable-session/canonical-path-v1-golden.json", "utf8")) as {
    version: number;
    vectors: Array<{ canonicalProjectPath: string; displayProjectPath: string; identityHash: string; instanceId: string }>;
  };
  assert.equal(fixture.version, CANONICALIZATION_VERSION);
  for (const vector of fixture.vectors) {
    const identity = resolveProjectIdentity(vector.displayProjectPath, () => ({ success: true, canonicalizationVersion: 1, ...vector }));
    assert.deepEqual(identity, { canonicalizationVersion: 1, ...vector });
  }
});

test("项目移动后 instanceId 改变", async () => {
  const firstBase = await mkdtemp(path.join(tmpdir(), "unity-cli-identity-a-"));
  const secondBase = await mkdtemp(path.join(tmpdir(), "unity-cli-identity-b-"));
  await createUnityProject(firstBase);
  await createUnityProject(secondBase);
  try {
    clearProjectIdentityCache();
    assert.notEqual(resolveProjectIdentity(firstBase).instanceId, resolveProjectIdentity(secondBase).instanceId);
  } finally {
    await Promise.all([rm(firstBase, { recursive: true, force: true }), rm(secondBase, { recursive: true, force: true })]);
    clearProjectIdentityCache();
  }
});

test("TS 对 native helper hash 不一致 fail closed", () => {
  const nativeResolver: NativeCanonicalProjectPathResolver = () => ({
    success: true,
    canonicalizationVersion: 1,
    canonicalProjectPath: "c:\\workspace\\game",
    displayProjectPath: "C:\\workspace\\Game",
    identityHash: "0".repeat(64),
    instanceId: `unity-${"0".repeat(64)}`,
  });
  assert.throws(
    () => resolveProjectIdentity("C:\\workspace\\Game", nativeResolver),
    (error: unknown) => error instanceof ProjectIdentityError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
});

test("async CanonicalPath resolver stops at the supplied observation budget", async () => {
  const delayedResolver: AsyncNativeCanonicalProjectPathResolver = async () => {
    await new Promise((complete) => setTimeout(complete, 250));
    return { success: false, canonicalizationVersion: 1, errorCode: "E_INSTANCE_PATH_UNAVAILABLE" };
  };
  const startedAt = Date.now();
  await assert.rejects(
    resolveProjectIdentityAsync("C:\\workspace\\Game", { timeoutMs: 40 }, delayedResolver),
    (error: unknown) => error instanceof ProjectIdentityError && error.code === "E_INSTANCE_PATH_UNAVAILABLE",
  );
  assert.ok(Date.now() - startedAt < 150);
});

test("remote、case-sensitive 与不可访问路径保留稳定 identity error", () => {
  for (const code of ["E_INSTANCE_REMOTE_PATH_UNSUPPORTED", "E_INSTANCE_PATH_CASE_SENSITIVE_UNSUPPORTED", "E_INSTANCE_PATH_UNAVAILABLE"] as const) {
    const nativeResolver: NativeCanonicalProjectPathResolver = () => ({
      success: false,
      canonicalizationVersion: 1,
      errorCode: code,
      errorMessage: code,
    });
    assert.throws(
      () => resolveProjectIdentity("C:\\workspace\\Game", nativeResolver),
      (error: unknown) => error instanceof ProjectIdentityError && error.code === code,
    );
  }
});

test("非 Unity 目录返回 E_INSTANCE_REQUIRED", async () => {
  const directory = await mkdtemp(path.join(tmpdir(), "unity-cli-not-project-"));
  try {
    clearProjectIdentityCache();
    assert.throws(
      () => resolveProjectIdentity(directory),
      (error: unknown) => error instanceof ProjectIdentityError && error.code === "E_INSTANCE_REQUIRED",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
    clearProjectIdentityCache();
  }
});
