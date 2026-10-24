import { spawn } from "node:child_process";
import { lstat, mkdir, readFile, readdir, rename, rm, stat, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { POWERSHELL_UTF8_OUTPUT_LINE } from "../powershell-transport.js";

// ─── Types ───────────────────────────────────────────────────────────────────

/** Supported agent identifiers for skill installation. */
export type AgentKind = "claude" | ".agents";

/** All valid agent kinds. */
export const AGENT_KINDS: readonly AgentKind[] = ["claude", ".agents"];

/** 一个明确Skill bundle目标的安装结果。 */
export interface SkillTargetInstallResult {
  targetPath: string;
  status: "installed" | "skipped" | "updated";
  hash?: string;
  previousHash?: string;
  newHash?: string;
  /** 本次更新从受管理目录移除的旧投影文件；用于告知用户自有内容应放在 `user/`。 */
  removedFiles?: string[];
}

/** 一个预设agent目标的安装结果。 */
export interface SkillInstallResult extends SkillTargetInstallResult {
  agent: AgentKind;
}

/** 一个明确Skill bundle目标的检查结果。 */
export interface SkillTargetCheckResult {
  targetPath: string;
  status: "installed" | "missing" | "outdated";
  hash?: string;
  sourceHash?: string;
}

/** 一个预设agent目标的检查结果。 */
export interface SkillCheckResult extends SkillTargetCheckResult {
  agent: AgentKind;
}

export interface SkillTargetInspection {
  status: "installed" | "missing" | "drifted" | "damaged";
  targetPath: string;
  sourceHash: string;
  hash?: string;
  message?: string;
}

// ─── Agent skill target paths ────────────────────────────────────────────────

/** Maps an agent kind to its skill directory relative to the Unity project root. */
const AGENT_SKILL_DIR: Record<AgentKind, string> = {
  claude: ".claude/skills/unitycli",
  ".agents": ".agents/skills/unitycli",
};

export function resolveSkillTarget(basePath: string, agent: AgentKind): string {
  return join(basePath, AGENT_SKILL_DIR[agent]);
}

// ─── Skill source resolution ─────────────────────────────────────────────────

/**
 * Resolve the source SKILL.md path by walking up from the current CLI module
 * location to find the package root that contains
 * skills/unitycli/SKILL.md.
 */
export function resolveSkillSourcePath(metaUrl?: string): string {
  const startDir = metaUrl
    ? dirname(fileURLToPath(new URL(metaUrl)))
    : dirname(fileURLToPath(import.meta.url));

  let current = resolve(startDir);
  // Walk up at most 20 levels to find the package root
  for (let i = 0; i < 20; i++) {
    const pkgPath = join(current, "package.json");
    try {
      // Synchronous existence check via require is not available in ESM;
      // we do a best-effort heuristic: check if skills/unitycli/SKILL.md exists.
      const skillPath = join(current, "skills", "unitycli", "SKILL.md");
      // We validate the actual content in resolveSkillSource (async)
      // For now, return the candidate path
      return skillPath;
    } catch {
      // Not found, walk up
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  // Fallback: use import.meta.url resolution
  return join(dirname(fileURLToPath(import.meta.url)), "..", "..", "skills", "unitycli", "SKILL.md");
}

/**
 * Async version: resolves the SKILL.md source path, validating that the
 * package root contains the published UnityCLI Skill bundle.
 */
export async function findSkillSourcePath(startDir?: string): Promise<string> {
  let current = startDir ?? dirname(fileURLToPath(import.meta.url));

  for (let i = 0; i < 20; i++) {
    const skillPath = join(current, "skills", "unitycli", "SKILL.md");
    try {
      // Try to read the skill file — if it exists, that's our source
      await readFile(skillPath, "utf8");
      return skillPath;
    } catch {
      // Not found at this level, walk up
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  throw new SkillInstallerError(
    "SKILL_SOURCE_NOT_FOUND",
    "Could not locate skills/unitycli/SKILL.md relative to the CLI package root.",
    ["Ensure the @alpsckr/unitycli package is installed with the skills/ directory intact."]
  );
}

// ─── Runtime bundle projection ────────────────────────────────────────────────

const runtimeDirectories = ["references", "scripts"] as const;
const feedbackModeDeclaration = /^FeedbackMode:\s*(.*)$/gm;
const validFeedbackMode = /^FeedbackMode: (off|on)$/;
let beforeSkillPublishForTest: (() => Promise<void>) | undefined;
let afterSkillPublishForTest: (() => Promise<void>) | undefined;
let beforeSkillRollbackForTest: (() => Promise<void>) | undefined;
let beforeSkillPreviousCleanupForTest: (() => Promise<void>) | undefined;
let beforeSkillTargetLoadForTest: (() => Promise<void>) | undefined;

/** 仅测试使用：在读取既有target前制造底层文件系统失败。 */
export function setBeforeSkillTargetLoadForTest(hook: (() => Promise<void>) | undefined): void {
  beforeSkillTargetLoadForTest = hook;
}

/** 仅测试使用：在 target 保持完整时、首次发布写入前制造等待或失败。 */
export function setBeforeSkillPublishForTest(hook: (() => Promise<void>) | undefined): void {
  beforeSkillPublishForTest = hook;
}

/** 仅测试使用：在 staging 已发布、readback 前制造损坏。 */
export function setAfterSkillPublishForTest(hook: (() => Promise<void>) | undefined): void {
  afterSkillPublishForTest = hook;
}

/** 仅测试使用：验证 rollback 失败时 previous 必须保留。 */
export function setBeforeSkillRollbackForTest(hook: (() => Promise<void>) | undefined): void {
  beforeSkillRollbackForTest = hook;
}

/** 仅测试使用：模拟新 target 已提交、previous 尚未清理时进程中断。 */
export function setBeforeSkillPreviousCleanupForTest(hook: (() => Promise<void>) | undefined): void {
  beforeSkillPreviousCleanupForTest = hook;
}

type FeedbackMode = "off" | "on";

interface SkillBundleFile {
  path: string;
  content: Buffer;
}

interface SkillBundle {
  files: SkillBundleFile[];
  hash: string;
  feedbackMode: FeedbackMode;
}

/** 读取唯一 Skill 文件开关；旧单文件投影无标记时只按关闭状态迁移。 */
function readFeedbackMode(content: Buffer, allowMissing: boolean): FeedbackMode {
  const text = decodeUtf8(content, "SKILL.md");
  const declarations = [...text.matchAll(feedbackModeDeclaration)].map((match) => match[0]);
  if (declarations.length === 0 && allowMissing) return "off";
  if (declarations.length !== 1 || !validFeedbackMode.test(declarations[0])) {
    throw new SkillInstallerError(
      "E_SKILL_FEEDBACK_MODE_INVALID",
      "The UnityCLI Skill must contain exactly one `FeedbackMode: off|on` declaration.",
      ["Keep one FeedbackMode declaration in SKILL.md and set it to off or on."]
    );
  }
  return declarations[0].endsWith("on") ? "on" : "off";
}

/** bundle digest 忽略合法本地开关值，其余字节和文件集合全部进入契约。 */
function normalizedSkillContent(file: SkillBundleFile): Buffer {
  if (file.path !== "SKILL.md") return file.content;
  return Buffer.from(decodeUtf8(file.content, file.path).replace(/^FeedbackMode: (?:off|on)$/m, "FeedbackMode: off"), "utf8");
}

/** 按排序后的相对路径、字节数和归一化内容计算 bundle digest。 */
function bundleHash(files: SkillBundleFile[]): string {
  const hash = createHash("sha256");
  for (const file of files) {
    const content = normalizedSkillContent(file);
    hash.update(`file:${file.path}\0${content.byteLength}\0`);
    hash.update(content);
    hash.update("\n");
  }
  return hash.digest("hex");
}

/** 收集 runtime Skill 投影；symlink 和非普通文件一律 fail closed。 */
async function collectRuntimeFiles(root: string): Promise<SkillBundleFile[]> {
  const files: SkillBundleFile[] = [];
  const rootInfo = await lstat(root);
  if (rootInfo.isSymbolicLink() || !rootInfo.isDirectory()) {
    throw new SkillInstallerError("E_SKILL_BUNDLE_INVALID", `Skill runtime root must be a real directory: ${root}`);
  }
  const skillPath = join(root, "SKILL.md");
  const skillInfo = await lstat(skillPath);
  if (skillInfo.isSymbolicLink() || !skillInfo.isFile()) {
    throw new SkillInstallerError("E_SKILL_BUNDLE_INVALID", "Skill runtime SKILL.md must be a regular file.");
  }
  files.push({ path: "SKILL.md", content: await readFile(skillPath) });

  const visit = async (directory: string, allowMissing = false): Promise<void> => {
    const absolute = join(root, directory);
    let directoryInfo;
    try {
      directoryInfo = await lstat(absolute);
    } catch (error) {
      if (allowMissing && (error as NodeJS.ErrnoException).code === "ENOENT") return;
      throw error;
    }
    if (directoryInfo.isSymbolicLink() || !directoryInfo.isDirectory()) {
      throw new SkillInstallerError("E_SKILL_BUNDLE_INVALID", `Skill runtime directory must not be a reparse link: ${directory}`);
    }
    const entries = await readdir(absolute, { withFileTypes: true });
    for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
      const absoluteEntry = join(absolute, entry.name);
      const relativeEntry = normalizeBundlePath(relative(root, absoluteEntry));
      if (entry.isSymbolicLink()) {
        throw new SkillInstallerError("E_SKILL_BUNDLE_INVALID", `Skill runtime bundle cannot contain a symbolic link: ${relativeEntry}`);
      }
      if (entry.isDirectory()) {
        await visit(relativeEntry);
      } else if (entry.isFile()) {
        files.push({ path: relativeEntry, content: await readFile(absoluteEntry) });
      } else {
        throw new SkillInstallerError("E_SKILL_BUNDLE_INVALID", `Skill runtime bundle contains an unsupported entry: ${relativeEntry}`);
      }
    }
  };

  for (const directory of runtimeDirectories) await visit(directory, true);
  return files.sort((left, right) => left.path.localeCompare(right.path));
}

/** 加载一个 runtime bundle，并返回文件集合、digest 与本地反馈开关。 */
async function loadBundle(root: string, allowMissingFeedbackMode: boolean): Promise<SkillBundle> {
  const files = await collectRuntimeFiles(root);
  const skill = files.find((file) => file.path === "SKILL.md")!;
  return {
    files,
    hash: bundleHash(files),
    feedbackMode: readFeedbackMode(skill.content, allowMissingFeedbackMode),
  };
}

/** 既有非空目录只有包含Skill入口时才证明归本安装器所有。 */
async function assertTargetOwnedOrEmpty(targetRoot: string, deadlineMs?: number): Promise<void> {
  if (!await exists(targetRoot, deadlineMs) || await exists(join(targetRoot, "SKILL.md"), deadlineMs)) return;
  const entries = await readdir(targetRoot);
  if (entries.length > 0) {
    throw new SkillInstallerError(
      "E_SKILL_TARGET_OWNERSHIP_UNPROVEN",
      "The selected Skill target is non-empty but does not contain a UnityCLI SKILL.md entry.",
      ["Choose the exact UnityCLI Skill directory or empty the intended target before installing."],
    );
  }
}

function isRepairableSkillDamage(error: unknown): error is SkillInstallerError {
  return error instanceof SkillInstallerError
    && (error.code === "E_SKILL_BUNDLE_INVALID" || error.code === "E_SKILL_FEEDBACK_MODE_INVALID");
}

function skillTargetReadError(error: unknown): SkillInstallerError {
  if (error instanceof SkillInstallerError) return error;
  const code = (error as NodeJS.ErrnoException).code;
  return new SkillInstallerError(
    "E_SKILL_TARGET_UNREADABLE",
    `The selected UnityCLI Skill target could not be read${code ? ` (${code})` : ""}.`,
    ["Close processes holding the Skill files, restore read access, then run the same check again."],
  );
}

async function loadTargetBundle(targetRoot: string): Promise<SkillBundle> {
  await beforeSkillTargetLoadForTest?.();
  return loadBundle(targetRoot, true);
}

/** 不获取安装 mutex、不写文件，只读比较一个明确 Skill target 与 bundled source。 */
export async function inspectSkillTarget(sourcePath: string, targetPath: string): Promise<SkillTargetInspection> {
  const source = await loadBundle(dirname(sourcePath), false);
  try {
    await assertTargetOwnedOrEmpty(targetPath);
    if (!await exists(join(targetPath, "SKILL.md"))) return { status: "missing", targetPath, sourceHash: source.hash };
    const target = await loadTargetBundle(targetPath);
    return target.hash === source.hash
      ? { status: "installed", targetPath, sourceHash: source.hash, hash: target.hash }
      : { status: "drifted", targetPath, sourceHash: source.hash, hash: target.hash };
  } catch (error) {
    if (isRepairableSkillDamage(error)) return { status: "damaged", targetPath, sourceHash: source.hash, message: error.message };
    throw skillTargetReadError(error);
  }
}

/** 把目标本地反馈开关投影到待发布 SKILL.md，其余文件保持原字节。 */
function renderBundleFile(file: SkillBundleFile, feedbackMode: FeedbackMode): Buffer {
  if (file.path !== "SKILL.md") return file.content;
  const text = decodeUtf8(file.content, file.path);
  return Buffer.from(text.replace(/^FeedbackMode: (?:off|on)$/m, `FeedbackMode: ${feedbackMode}`), "utf8");
}

/** 严格解码 SKILL.md；非 UTF-8 字节不得被替换字符吞并。 */
function decodeUtf8(content: Buffer, path: string): string {
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(content);
  } catch {
    throw new SkillInstallerError("E_SKILL_BUNDLE_INVALID", `Skill runtime file must be valid UTF-8: ${path}`);
  }
}

/** 只读判断路径是否存在；安装路径上的 share-deny 服从同一 deadline。 */
async function exists(path: string, deadlineMs?: number): Promise<boolean> {
  while (true) {
    try {
      await stat(path);
      return true;
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code === "ENOENT") return false;
      if (deadlineMs === undefined || !code || !sharingViolationCodes.has(code) || Date.now() >= deadlineMs) throw error;
      await new Promise((resolveDelay) => setTimeout(resolveDelay, 25));
    }
  }
}

/** 将 bundle 相对路径统一为跨平台正斜杠形式。 */
function normalizeBundlePath(path: string): string {
  return path.split("\\").join("/");
}

const sharingViolationCodes = new Set(["EACCES", "EBUSY", "EPERM"]);
const defaultInstallTimeoutMs = 15_000;
const localInstallQueues = new Map<string, Promise<void>>();

/** 在任何安装 effect 前执行 immutable deadline barrier。 */
function assertInstallDeadline(deadlineMs: number): void {
  if (Date.now() >= deadlineMs) {
    throw new SkillInstallerError(
      "E_EXECUTION_TIMEOUT",
      "The immutable init deadline elapsed before the next Skill bundle operation.",
      ["Inspect the installed Skill bundle before submitting init again."]
    );
  }
}

/** deadline 内只重试 Windows 临时 share-deny；其他文件系统错误立即返回。 */
async function retrySharingViolation<T>(deadlineMs: number, action: () => Promise<T>): Promise<T> {
  while (true) {
    assertInstallDeadline(deadlineMs);
    try {
      return await action();
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (!code || !sharingViolationCodes.has(code) || Date.now() >= deadlineMs) throw error;
      await new Promise((resolveDelay) => setTimeout(resolveDelay, 25));
    }
  }
}

/** 把底层文件系统失败转换为稳定、可执行的 Skill 安装错误。 */
function skillInstallIoError(error: unknown, operation: string): SkillInstallerError {
  if (error instanceof SkillInstallerError) return error;
  const code = (error as NodeJS.ErrnoException).code;
  return new SkillInstallerError(
    "E_SKILL_INSTALL_IO_EXCEPTION",
    `The UnityCLI Skill bundle could not ${operation}${code ? ` (${code})` : ""}.`,
    ["Close processes holding the installed Skill files, inspect the target bundle, then retry init."]
  );
}

/** 非 Windows 仅在当前 CLI 进程内串行；Windows 使用跨进程 named mutex。 */
async function withLocalInstallQueue<T>(key: string, deadlineMs: number, action: () => Promise<T>): Promise<T> {
  const prior = localInstallQueues.get(key) ?? Promise.resolve();
  let release!: () => void;
  const current = new Promise<void>((resolveRelease) => { release = resolveRelease; });
  const tail = prior.then(() => current);
  localInstallQueues.set(key, tail);
  await prior;
  assertInstallDeadline(deadlineMs);
  try {
    return await action();
  } finally {
    release();
    if (localInstallQueues.get(key) === tail) localInstallQueues.delete(key);
  }
}

/** 获取 Windows kernel mutex；持有者绑定父进程 PID 与 StartTime，父进程结束后自动释放。 */
async function withWindowsInstallMutex<T>(targetRoot: string, deadlineMs: number, action: () => Promise<T>): Promise<T> {
  assertInstallDeadline(deadlineMs);
  const key = createHash("sha256").update(resolve(targetRoot).toLowerCase()).digest("hex").slice(0, 32);
  const mutexName = `Local\\UnityCliSkillInstall_${key}`;
  const script = [
    POWERSHELL_UTF8_OUTPUT_LINE,
    "$mutex = [System.Threading.Mutex]::new($false, $env:UNITY_CLI_SKILL_MUTEX_NAME)",
    "$parent = Get-Process -Id ([int]$env:UNITY_CLI_SKILL_PARENT_PID) -ErrorAction Stop",
    "$parentStartTicks = $parent.StartTime.ToUniversalTime().Ticks",
    "$held = $false",
    "try {",
    "  $remaining = [int64]$env:UNITY_CLI_SKILL_DEADLINE_MS - [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()",
    "  if ($remaining -le 0) { [Console]::Out.WriteLine('TIMEOUT'); exit 2 }",
    "  $wait = [int][Math]::Min([int]::MaxValue, $remaining)",
    "  try { $held = $mutex.WaitOne($wait) } catch [System.Threading.AbandonedMutexException] { $held = $true }",
    "  if (-not $held) { [Console]::Out.WriteLine('TIMEOUT'); exit 2 }",
    "  [Console]::Out.WriteLine('ACQUIRED'); [Console]::Out.Flush()",
    "  while ($true) {",
    "    try { $current = Get-Process -Id ([int]$env:UNITY_CLI_SKILL_PARENT_PID) -ErrorAction Stop } catch { break }",
    "    if ($current.StartTime.ToUniversalTime().Ticks -ne $parentStartTicks) { break }",
    "    Start-Sleep -Milliseconds 100",
    "  }",
    "} finally {",
    "  if ($held) { try { $mutex.ReleaseMutex() } catch {} }",
    "  $mutex.Dispose()",
    "}",
  ].join("\n");
  const child = spawn("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], {
    env: {
      ...process.env,
      UNITY_CLI_SKILL_MUTEX_NAME: mutexName,
      UNITY_CLI_SKILL_DEADLINE_MS: String(deadlineMs),
      UNITY_CLI_SKILL_PARENT_PID: String(process.pid),
    },
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });
  let stderr = "";
  child.stderr.setEncoding("utf8");
  child.stderr.on("data", (chunk: string) => { stderr += chunk; });
  const exitPromise = new Promise<number | null>((resolveExit) => child.once("exit", resolveExit));
  await new Promise<void>((resolveAcquired, rejectAcquired) => {
    let stdout = "";
    let settled = false;
    const fail = (error: Error): void => {
      if (settled) return;
      settled = true;
      rejectAcquired(error);
    };
    child.once("error", (error) => fail(new SkillInstallerError("E_SKILL_INSTALL_LOCK_UNAVAILABLE", `Could not start the Windows Skill install mutex: ${error.message}`)));
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => {
      if (settled) return;
      stdout += chunk;
      const line = stdout.split(/\r?\n/, 1)[0];
      if (line === "ACQUIRED") {
        settled = true;
        resolveAcquired();
      } else if (line === "TIMEOUT") {
        fail(new SkillInstallerError(
          "E_EXECUTION_TIMEOUT",
          "The immutable init deadline elapsed while waiting for another UnityCLI Skill install.",
          ["Inspect the installed Skill bundle before submitting init again."]
        ));
      }
    });
    child.once("exit", (code) => fail(new SkillInstallerError("E_SKILL_INSTALL_LOCK_UNAVAILABLE", `The Windows Skill install mutex exited before acquisition (${code}): ${stderr.trim()}`)));
  });

  try {
    assertInstallDeadline(deadlineMs);
    return await action();
  } finally {
    child.kill();
    await exitPromise;
  }
}

/** 按 project + agent 串行完整 bundle 发布。 */
async function withSkillInstallMutex<T>(targetRoot: string, deadlineMs: number, action: () => Promise<T>): Promise<T> {
  if (process.platform === "win32") return withWindowsInstallMutex(targetRoot, deadlineMs, action);
  return withLocalInstallQueue(resolve(targetRoot), deadlineMs, action);
}

interface InstallWorkspace {
  stagingRoot: string;
  previousRoot: string;
}

interface CommittedTargetMarker {
  schemaVersion: 1;
  targetHash: string;
  feedbackMode: FeedbackMode;
}

const committedTargetMarkerName = "committed-target.json";

/** 返回固定 workspace；每个 project + agent 最多只有 staging 和 previous 两个机制目录。 */
function installWorkspace(projectRoot: string, agent: AgentKind, explicitRoot?: string): InstallWorkspace {
  const root = explicitRoot ?? join(projectRoot, "Library", "UnityCli", "skill-install", agent === ".agents" ? "agents" : agent);
  return { stagingRoot: join(root, "staging"), previousRoot: join(root, "previous") };
}

/** 将内存中的完整 bundle 写入固定 workspace，并以严格 readback 验证。 */
async function materializeBundle(bundle: SkillBundle, feedbackMode: FeedbackMode, root: string, deadlineMs: number): Promise<void> {
  await retrySharingViolation(deadlineMs, () => rm(root, { recursive: true, force: true }));
  assertInstallDeadline(deadlineMs);
  await mkdir(root, { recursive: true });
  for (const file of bundle.files) {
    assertInstallDeadline(deadlineMs);
    const target = join(root, ...file.path.split("/"));
    await mkdir(dirname(target), { recursive: true });
    await writeFile(target, renderBundleFile(file, feedbackMode));
  }
  const materialized = await loadBundle(root, false);
  if (materialized.hash !== bundle.hash || materialized.feedbackMode !== feedbackMode) {
    throw new SkillInstallerError("E_SKILL_INSTALL_PROJECTION_INVALID", "The materialized UnityCLI Skill bundle failed filesystem readback.");
  }
}

/**
 * 逐文件原子替换已校验 staging，最后发布 SKILL.md，再删除新入口不再引用的旧文件。
 * target 根目录与每个既有文件在 update 期间始终存在，外部读取者不会遇到目录隔离窗口。
 */
async function publishStagedFiles(bundle: SkillBundle, stagingRoot: string, targetRoot: string, deadlineMs: number): Promise<string[]> {
  await mkdir(targetRoot, { recursive: true });
  const ordered = [...bundle.files].sort((left, right) => {
    if (left.path === "SKILL.md") return 1;
    if (right.path === "SKILL.md") return -1;
    return left.path.localeCompare(right.path);
  });
  for (const file of ordered) {
    assertInstallDeadline(deadlineMs);
    const stagedPath = join(stagingRoot, ...file.path.split("/"));
    const targetPath = join(targetRoot, ...file.path.split("/"));
    await mkdir(dirname(targetPath), { recursive: true });
    await retrySharingViolation(deadlineMs, () => rename(stagedPath, targetPath));
  }

  const desiredPaths = new Set(bundle.files.map((file) => file.path));
  const publishedFiles = await collectRuntimeFiles(targetRoot);
  const removed: string[] = [];
  for (const file of publishedFiles) {
    if (desiredPaths.has(file.path)) continue;
    assertInstallDeadline(deadlineMs);
    await retrySharingViolation(deadlineMs, () => rm(join(targetRoot, ...file.path.split("/")), { force: true }));
    removed.push(file.path);
  }
  return removed;
}

/** previous 始终保留到旧 bundle 原位恢复并通过 readback；失败时不得删除备份。 */
async function restorePreviousBundle(targetRoot: string, workspace: InstallWorkspace, deadlineMs: number): Promise<void> {
  const recoveryDeadlineMs = Math.max(deadlineMs, Date.now() + 5_000);
  try {
    await beforeSkillRollbackForTest?.();
    const previous = await retrySharingViolation(recoveryDeadlineMs, () => loadBundle(workspace.previousRoot, false));
    await materializeBundle(previous, previous.feedbackMode, workspace.stagingRoot, recoveryDeadlineMs);
    await publishStagedFiles(previous, workspace.stagingRoot, targetRoot, recoveryDeadlineMs);
    const restored = await retrySharingViolation(recoveryDeadlineMs, () => loadBundle(targetRoot, false));
    if (restored.hash !== previous.hash || restored.feedbackMode !== previous.feedbackMode) {
      throw new SkillInstallerError("E_SKILL_INSTALL_PROJECTION_INVALID", "The restored UnityCLI Skill bundle failed filesystem readback.");
    }
    await retrySharingViolation(recoveryDeadlineMs, () => rm(workspace.previousRoot, { recursive: true, force: true }));
  } catch (rollbackError) {
    const detail = rollbackError instanceof Error ? rollbackError.message : String(rollbackError);
    throw new SkillInstallerError(
      "E_SKILL_INSTALL_ROLLBACK_FAILED",
      `The prior UnityCLI Skill bundle could not be restored after publish failed: ${detail}`,
      ["Do not delete the fixed Library/UnityCli/skill-install previous directory; inspect both bundle projections before retrying."]
    );
  }
}

/** 读取已验证 target 的固定提交标记；缺失、损坏或未知版本一律视为未提交。 */
async function readCommittedTargetMarker(previousRoot: string): Promise<CommittedTargetMarker | undefined> {
  try {
    const value = JSON.parse(await readFile(join(previousRoot, committedTargetMarkerName), "utf8")) as Partial<CommittedTargetMarker>;
    if (value.schemaVersion !== 1 || typeof value.targetHash !== "string" || !/^[a-f0-9]{64}$/.test(value.targetHash)) return undefined;
    if (value.feedbackMode !== "off" && value.feedbackMode !== "on") return undefined;
    return value as CommittedTargetMarker;
  } catch {
    return undefined;
  }
}

/** 重启时区分已提交 target 与未完成发布，再清理固定 workspace。 */
async function recoverInstallWorkspace(targetRoot: string, workspace: InstallWorkspace, deadlineMs: number): Promise<void> {
  await retrySharingViolation(deadlineMs, () => rm(workspace.stagingRoot, { recursive: true, force: true }));
  if (!await exists(workspace.previousRoot, deadlineMs)) return;

  const marker = await readCommittedTargetMarker(workspace.previousRoot);
  if (marker && await exists(targetRoot, deadlineMs)) {
    try {
      const target = await retrySharingViolation(deadlineMs, () => loadBundle(targetRoot, false));
      if (target.hash === marker.targetHash && target.feedbackMode === marker.feedbackMode) {
        await retrySharingViolation(deadlineMs, () => rm(workspace.previousRoot, { recursive: true, force: true }));
        return;
      }
    } catch {
      // target 无法严格 readback 时按未提交处理，使用 previous 恢复。
    }
  }

  await restorePreviousBundle(targetRoot, workspace, deadlineMs);
  await retrySharingViolation(deadlineMs, () => rm(workspace.stagingRoot, { recursive: true, force: true }));
}

/** 先校验完整 staging；fresh install 目录发布，update 采用入口最后的文件原子替换。 */
async function publishBundle(
  source: SkillBundle,
  existing: SkillBundle | undefined,
  feedbackMode: FeedbackMode,
  targetRoot: string,
  workspace: InstallWorkspace,
  deadlineMs: number,
): Promise<string[]> {
  let freshPublished = false;
  let targetCommitted = false;
  let removedFiles: string[] = [];
  try {
    await mkdir(dirname(targetRoot), { recursive: true });
    if (existing) {
      // previous 先在 staging 完整物化并校验，再一次 rename 提交，崩溃不会留下半备份。
      await materializeBundle(existing, existing.feedbackMode, workspace.stagingRoot, deadlineMs);
      await retrySharingViolation(deadlineMs, () => rm(workspace.previousRoot, { recursive: true, force: true }));
      await retrySharingViolation(deadlineMs, () => rename(workspace.stagingRoot, workspace.previousRoot));
    } else if (await exists(targetRoot, deadlineMs)) {
      await retrySharingViolation(deadlineMs, () => rm(targetRoot, { recursive: true, force: true }));
    }
    await materializeBundle(source, feedbackMode, workspace.stagingRoot, deadlineMs);

    await beforeSkillPublishForTest?.();
    assertInstallDeadline(deadlineMs);
    if (existing) {
      removedFiles = await publishStagedFiles(source, workspace.stagingRoot, targetRoot, deadlineMs);
    } else {
      await retrySharingViolation(deadlineMs, () => rename(workspace.stagingRoot, targetRoot));
      freshPublished = true;
    }
    await afterSkillPublishForTest?.();
    const published = await retrySharingViolation(deadlineMs, () => loadBundle(targetRoot, false));
    if (published.hash !== source.hash || published.feedbackMode !== feedbackMode) {
      throw new SkillInstallerError("E_SKILL_INSTALL_PROJECTION_INVALID", "The installed UnityCLI Skill bundle failed filesystem readback.");
    }
    if (existing) {
      const cleanupDeadlineMs = Math.max(deadlineMs, Date.now() + 5_000);
      const marker: CommittedTargetMarker = { schemaVersion: 1, targetHash: published.hash, feedbackMode: published.feedbackMode };
      await writeFile(join(workspace.previousRoot, committedTargetMarkerName), `${JSON.stringify(marker)}\n`, "utf8");
      targetCommitted = true;
      await beforeSkillPreviousCleanupForTest?.();
      await retrySharingViolation(cleanupDeadlineMs, () => rm(workspace.previousRoot, { recursive: true, force: true }));
    } else {
      targetCommitted = true;
    }
  } catch (error) {
    const recoveryDeadlineMs = Math.max(deadlineMs, Date.now() + 5_000);
    if (targetCommitted) {
      throw skillInstallIoError(error, "clean its committed previous runtime projection");
    }
    if (existing && await exists(workspace.previousRoot, recoveryDeadlineMs)) {
      await restorePreviousBundle(targetRoot, workspace, recoveryDeadlineMs);
      throw skillInstallIoError(error, "publish its verified runtime projection");
    }
    if (freshPublished && await exists(targetRoot, recoveryDeadlineMs)) {
      try {
        await retrySharingViolation(recoveryDeadlineMs, () => rm(targetRoot, { recursive: true, force: true }));
      } catch {
        throw skillInstallIoError(error, "remove an invalid fresh projection");
      }
    }
    throw skillInstallIoError(error, "publish its runtime projection");
  } finally {
    const cleanupDeadlineMs = Math.max(deadlineMs, Date.now() + 5_000);
    await retrySharingViolation(cleanupDeadlineMs, () => rm(workspace.stagingRoot, { recursive: true, force: true })).catch(() => undefined);
  }
  return removedFiles;
}

// ─── Installer interface ─────────────────────────────────────────────────────

/** 复用唯一bundle事务实现安装一个已经过调用层安全校验的明确目标。 */
async function installBundleAtTarget(
  sourcePath: string,
  targetRoot: string,
  workspace: InstallWorkspace,
  absoluteDeadlineMs: number,
): Promise<SkillTargetInstallResult> {
  const targetPath = join(targetRoot, "SKILL.md");
  const source = await loadBundle(dirname(sourcePath), false);
  return withSkillInstallMutex(targetRoot, absoluteDeadlineMs, async () => {
    try {
      await recoverInstallWorkspace(targetRoot, workspace, absoluteDeadlineMs);
      await assertTargetOwnedOrEmpty(targetRoot, absoluteDeadlineMs);
      let existing: SkillBundle | undefined;
      let replacedDamaged = false;
      if (await exists(targetPath, absoluteDeadlineMs)) {
        try {
          existing = await retrySharingViolation(absoluteDeadlineMs, () => loadTargetBundle(targetRoot));
        } catch (error) {
          if (!isRepairableSkillDamage(error)) throw error;
          if (await exists(workspace.previousRoot, absoluteDeadlineMs)) {
            await restorePreviousBundle(targetRoot, workspace, absoluteDeadlineMs);
            existing = await retrySharingViolation(absoluteDeadlineMs, () => loadTargetBundle(targetRoot));
          } else {
            replacedDamaged = true;
          }
        }
      }

      if (existing?.hash === source.hash) {
        await retrySharingViolation(absoluteDeadlineMs, () => rm(workspace.previousRoot, { recursive: true, force: true }));
        return { targetPath, status: "skipped", hash: source.hash };
      }
      if (existing && await exists(workspace.previousRoot, absoluteDeadlineMs)) {
        await retrySharingViolation(absoluteDeadlineMs, () => rm(workspace.previousRoot, { recursive: true, force: true }));
      }
      const feedbackMode = existing?.feedbackMode ?? source.feedbackMode;
      const removedFiles = await publishBundle(source, existing, feedbackMode, targetRoot, workspace, absoluteDeadlineMs);
      const removed = removedFiles.length > 0 ? { removedFiles } : {};
      if (existing || replacedDamaged) {
        return { targetPath, status: "updated", ...(existing ? { previousHash: existing.hash } : {}), newHash: source.hash, ...removed };
      }
      return { targetPath, status: "installed", hash: source.hash, ...removed };
    } catch (error) {
      throw skillInstallIoError(error, "complete its runtime bundle installation");
    }
  });
}

/** 明确目录和预设agent都通过同一mutex读取，避免检查观察到发布中间态。 */
async function checkBundleAtTarget(sourcePath: string, targetRoot: string, absoluteDeadlineMs: number): Promise<SkillTargetCheckResult> {
  const source = await loadBundle(dirname(sourcePath), false);
  return withSkillInstallMutex(targetRoot, absoluteDeadlineMs, async () => {
    const targetPath = join(targetRoot, "SKILL.md");
    if (!await exists(targetPath, absoluteDeadlineMs)) return { targetPath, status: "missing", sourceHash: source.hash };
    const installed = await retrySharingViolation(absoluteDeadlineMs, () => loadTargetBundle(targetRoot));
    return installed.hash === source.hash
      ? { targetPath, status: "installed", hash: installed.hash, sourceHash: source.hash }
      : { targetPath, status: "outdated", hash: installed.hash, sourceHash: source.hash };
  });
}

export async function installSkillTarget(
  sourcePath: string,
  targetRoot: string,
  installRoot: string,
  absoluteDeadlineMs = Date.now() + defaultInstallTimeoutMs,
): Promise<SkillTargetInstallResult> {
  const workspace = { stagingRoot: join(installRoot, "staging"), previousRoot: join(installRoot, "previous") };
  return installBundleAtTarget(sourcePath, resolve(targetRoot), workspace, absoluteDeadlineMs);
}

export async function checkSkillTarget(
  sourcePath: string,
  targetRoot: string,
  absoluteDeadlineMs = Date.now() + defaultInstallTimeoutMs,
): Promise<SkillTargetCheckResult> {
  return checkBundleAtTarget(sourcePath, resolve(targetRoot), absoluteDeadlineMs);
}

export interface SkillInstaller {
  /** Agent kind this installer handles. */
  readonly agent: AgentKind;
  /** Resolve the target SKILL.md path for the given project root. */
  resolveTargetPath(projectRoot: string): string;
  /** Install the complete runtime Skill bundle within one immutable deadline. */
  install(sourcePath: string, projectRoot: string, absoluteDeadlineMs?: number, installRoot?: string): Promise<SkillInstallResult>;
  /** Check the complete runtime Skill bundle without writing. */
  check(sourcePath: string, projectRoot: string, absoluteDeadlineMs?: number): Promise<SkillCheckResult>;
}

// ─── Base installer implementation ───────────────────────────────────────────

class BaseSkillInstaller implements SkillInstaller {
  constructor(readonly agent: AgentKind) {}

  resolveTargetPath(projectRoot: string): string {
    return join(projectRoot, AGENT_SKILL_DIR[this.agent], "SKILL.md");
  }

  async install(sourcePath: string, projectRoot: string, absoluteDeadlineMs = Date.now() + defaultInstallTimeoutMs, installRoot?: string): Promise<SkillInstallResult> {
    const targetRoot = dirname(this.resolveTargetPath(projectRoot));
    const result = await installBundleAtTarget(sourcePath, targetRoot, installWorkspace(projectRoot, this.agent, installRoot), absoluteDeadlineMs);
    return { agent: this.agent, ...result };
  }

  async check(
    sourcePath: string,
    projectRoot: string,
    absoluteDeadlineMs = Date.now() + defaultInstallTimeoutMs,
  ): Promise<SkillCheckResult> {
    const targetRoot = dirname(this.resolveTargetPath(projectRoot));
    return { agent: this.agent, ...await checkBundleAtTarget(sourcePath, targetRoot, absoluteDeadlineMs) };
  }
}

// ─── Registry ─────────────────────────────────────────────────────────────────

export class AgentSkillInstallerRegistry {
  private readonly installers: Map<AgentKind, SkillInstaller>;

  constructor() {
    this.installers = new Map<AgentKind, SkillInstaller>();
    // Register built-in installers for all supported agent kinds
    for (const agent of AGENT_KINDS) {
      this.installers.set(agent, new BaseSkillInstaller(agent));
    }
  }

  /** Get the installer for a specific agent kind. */
  get(agent: AgentKind): SkillInstaller | undefined {
    return this.installers.get(agent);
  }

  /** Get all registered agent kinds. */
  agents(): AgentKind[] {
    return [...this.installers.keys()];
  }

  /**
   * Install the skill for the specified agents.
   * @param agents Comma-separated or array of agent kinds (e.g. "claude,.agents")
   */
  async installForAgents(
    agents: string | AgentKind[],
    sourcePath: string,
    projectRoot: string,
    absoluteDeadlineMs?: number,
    installRoot?: string,
  ): Promise<SkillInstallResult[]> {
    const resolved = parseAgentList(agents);
    const results: SkillInstallResult[] = [];

    for (const agent of resolved) {
      const installer = this.installers.get(agent);
      if (!installer) {
        throw new SkillInstallerError(
          "UNKNOWN_AGENT_KIND",
          `Unknown agent kind: ${agent}. Supported: ${AGENT_KINDS.join(", ")}`,
          [`Use one of: ${AGENT_KINDS.join(", ")}`]
        );
      }
      results.push(await installer.install(sourcePath, projectRoot, absoluteDeadlineMs, installRoot));
    }

    return results;
  }

  /**
   * Check skill status for the specified agents.
   * @param agents Comma-separated or array of agent kinds
   */
  async checkForAgents(
    agents: string | AgentKind[],
    sourcePath: string,
    projectRoot: string,
    absoluteDeadlineMs?: number,
  ): Promise<SkillCheckResult[]> {
    const resolved = parseAgentList(agents);
    const results: SkillCheckResult[] = [];

    for (const agent of resolved) {
      const installer = this.installers.get(agent);
      if (!installer) {
        throw new SkillInstallerError(
          "UNKNOWN_AGENT_KIND",
          `Unknown agent kind: ${agent}. Supported: ${AGENT_KINDS.join(", ")}`,
          [`Use one of: ${AGENT_KINDS.join(", ")}`]
        );
      }
      results.push(await installer.check(sourcePath, projectRoot, absoluteDeadlineMs));
    }

    return results;
  }
}

// ─── Agent list parsing ──────────────────────────────────────────────────────

/**
 * Parse a comma-separated string or array of agent kinds into validated AgentKind[].
 */
export function parseAgentList(agents: string | AgentKind[]): AgentKind[] {
  const raw = typeof agents === "string"
    ? agents.split(",").map((a) => a.trim()).filter(Boolean)
    : agents;

  const validSet = new Set<string>(AGENT_KINDS);
  const result: AgentKind[] = [];

  for (const agent of raw) {
    if (!validSet.has(agent)) {
      throw new SkillInstallerError(
        "UNKNOWN_AGENT_KIND",
        `Unknown agent kind: "${agent}". Supported: ${AGENT_KINDS.join(", ")}`,
        [`Use one or more of: ${AGENT_KINDS.join(", ")}, comma-separated (e.g. "claude,.agents")`]
      );
    }
    // Deduplicate
    if (!result.includes(agent as AgentKind)) {
      result.push(agent as AgentKind);
    }
  }

  if (result.length === 0) {
    throw new SkillInstallerError(
      "AGENT_KIND_REQUIRED",
      "At least one agent kind must be specified.",
      [`Use --agent with one or more of: ${AGENT_KINDS.join(", ")}`]
    );
  }

  return result;
}

// ─── Error class ─────────────────────────────────────────────────────────────

export class SkillInstallerError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly nextActions: string[] = []
  ) {
    super(message);
  }
}

// ─── Convenience: default registry instance ──────────────────────────────────

/** Default registry with all built-in agent installers. */
export const agentSkillInstaller = new AgentSkillInstallerRegistry();
