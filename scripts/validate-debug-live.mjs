#!/usr/bin/env node
/**
 * Dev 专用验证 harness：为通用 Editor 运行时调试系统（ISSUE-084 / T-18）安装可复用夹具、
 * 同步 canonical Package、读取 Dev Editor 状态，并核验正式根隔离。
 *
 * 隔离硬约束（本文件的实现依据）：
 * 1. 所有 Dev 验证只用数据根 `%LOCALAPPDATA%\UnityCli-Dev`；本脚本只向该根写入，
 *    固定文件名、每次覆盖，不产生按请求/日期累积的记录。
 * 2. `UNITYCLI_DATA_DIR` 只注入本脚本派生的子进程环境，绝不写用户级或系统级环境变量，绝不调用 setx。
 * 3. 正式根 `%LOCALAPPDATA%\UnityCli` 只读盘点；本脚本对它没有任何写路径。
 * 4. 正式 Unity 项目（包括用户真实项目）只读进程元数据；本脚本不启动、不关闭、不重启任何 Editor，
 *    唯一允许的 Editor 关闭动作是调用方显式传入 `--close-editor` 时由 CLI 正常关闭 Dev 项目。
 * 5. 所有路径为绝对路径；仓库根从脚本位置解析，不使用 process.cwd()。
 */
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { cp, mkdir, readdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

// ── 固定位置事实 ───────────────────────────────────────────────────────────
const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(SCRIPT_DIR, "..");
const CLI_ENTRY = resolve(REPO_ROOT, "dist", "cli.js");
const FIXTURE_SOURCE_DIR = resolve(REPO_ROOT, "scripts", "live-matrix", "fixtures", "debug-fixture");
const DEV_PROJECT_DIR = resolve(REPO_ROOT, "Dev", "UnityProject");
const DEV_PROJECT_VERSION_FILE = resolve(DEV_PROJECT_DIR, "ProjectSettings", "ProjectVersion.txt");
const DEV_FIXTURE_DIR = resolve(DEV_PROJECT_DIR, "Assets", "UnityCLIDebugFixture");
const DEV_ASSETS_DIR = resolve(DEV_PROJECT_DIR, "Assets");

// ── 有界预算：保证单文件、stdout 和盘点时间不随使用次数增长 ────────────────
const MAX_INVENTORY_ENTRIES = 4000;
const MAX_UNITY_PROCESSES = 64;
const CLI_TIMEOUT_MS = 15 * 60 * 1000;
const STATE_TIMEOUT_MS = 15 * 1000;
const POWERSHELL_TIMEOUT_MS = 60 * 1000;

/** harness 自身失败使用稳定 code，便于调用方判断而不是解析文本。 */
class HarnessError extends Error {
  constructor(code, message, nextActions = [], data = undefined) {
    super(message);
    this.code = code;
    this.nextActions = nextActions;
    this.data = data;
  }
}

/** LOCALAPPDATA 缺失时不能猜测数据根，必须立即失败。 */
function dataRoots() {
  const localAppData = process.env.LOCALAPPDATA;
  if (!localAppData) {
    throw new HarnessError("E_HARNESS_NO_LOCALAPPDATA", "LOCALAPPDATA is not set, so the Dev and release data roots cannot be resolved.", [
      "Run this harness from a normal Windows user session where %LOCALAPPDATA% is defined."
    ]);
  }
  const devRoot = resolve(localAppData, "UnityCli-Dev");
  const releaseRoot = resolve(localAppData, "UnityCli");
  return {
    devRoot,
    releaseRoot,
    devTempDir: resolve(devRoot, ".temp"),
    // 固定文件名的盘点记录；每次运行覆盖同一路径，不形成逐次累积。
    baselineFile: resolve(devRoot, ".temp", "isolation-release-root.json"),
    afterFile: resolve(devRoot, ".temp", "isolation-release-root-after.json")
  };
}

const COMMANDS = new Set(["isolation-before", "install-fixture", "sync-package", "state", "isolation-after"]);
const USAGE = "node scripts/validate-debug-live.mjs <isolation-before|install-fixture|sync-package|state|isolation-after> [--dry-run] [--close-editor]";

async function exists(path) {
  try {
    await stat(path);
    return true;
  } catch {
    return false;
  }
}

function toPosix(relativePath) {
  return relativePath.split(sep).join("/");
}

/**
 * 有界递归盘点：只读取路径、大小和修改时间；不读内容，因此不会因盘点而触发 import 或写入。
 * fileSetHash 只覆盖相对路径集合，用于判定“正式根是否新增或丢失了记录”。
 */
async function inventoryRoot(root, limit = MAX_INVENTORY_ENTRIES) {
  if (!await exists(root)) return { root, exists: false, files: [], truncated: false, fileSetHash: null };
  const files = [];
  let truncated = false;
  const walk = async (current) => {
    if (files.length >= limit) {
      truncated = true;
      return;
    }
    let entries;
    try {
      entries = await readdir(current, { withFileTypes: true });
    } catch {
      return;
    }
    entries.sort((left, right) => (left.name < right.name ? -1 : left.name > right.name ? 1 : 0));
    for (const entry of entries) {
      if (files.length >= limit) {
        truncated = true;
        return;
      }
      const full = join(current, entry.name);
      if (entry.isDirectory()) {
        await walk(full);
        continue;
      }
      if (!entry.isFile()) continue;
      let info;
      try {
        info = await stat(full);
      } catch {
        continue;
      }
      files.push({ path: toPosix(relative(root, full)), size: info.size, modifiedMs: Math.floor(info.mtimeMs) });
    }
  };
  await walk(root);
  const fileSetHash = createHash("sha256").update(files.map((file) => file.path).join("\n")).digest("hex");
  return { root, exists: true, files, truncated, fileSetHash };
}

/** stdout 侧只返回摘要；完整清单只进固定盘点文件。 */
function inventorySummary(inventory) {
  return {
    root: inventory.root,
    exists: inventory.exists,
    fileCount: inventory.files.length,
    truncated: inventory.truncated,
    fileSetHash: inventory.fileSetHash
  };
}

function spawnOnce(file, args, options = {}) {
  return new Promise((finish) => {
    const child = spawn(file, args, {
      shell: false,
      windowsHide: true,
      cwd: options.cwd ?? REPO_ROOT,
      env: options.env ?? process.env,
      stdio: ["ignore", "pipe", "pipe"]
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("error", (error) => finish({ spawnFailed: true, error: String(error), stdout, stderr }));
    child.on("close", (exitCode) => finish({ spawnFailed: false, exitCode, stdout, stderr }));
  });
}

/** 读取 Unity Editor 进程元数据：只读，不控制、不终止任何进程。 */
async function listUnityProcesses() {
  const script = [
    "$ErrorActionPreference='Stop';",
    "$items = @(Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | Select-Object ProcessId,CreationDate,CommandLine);",
    "$items | ConvertTo-Json -Compress -Depth 3"
  ].join(" ");
  const args = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script];
  let result = null;
  for (const file of ["powershell.exe", "pwsh"]) {
    result = await withTimeout(spawnOnce(file, args), POWERSHELL_TIMEOUT_MS);
    if (!result.spawnFailed) break;
    if (!/ENOENT/.test(result.error)) break;
  }
  if (!result || result.spawnFailed) {
    throw new HarnessError("E_HARNESS_PROCESS_READ_FAILED", `Unity Editor process metadata could not be read: ${result?.error ?? "unknown error"}`, [
      "Run this harness on Windows with powershell.exe or pwsh available on PATH."
    ]);
  }
  const text = (result.stdout ?? "").trim();
  if (!text) return [];
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    throw new HarnessError("E_HARNESS_PROCESS_READ_FAILED", `Unity Editor process metadata was not valid JSON: ${String(error)}`, [
      "Rerun the harness; if it repeats, inspect the PowerShell command output manually."
    ]);
  }
  const list = Array.isArray(parsed) ? parsed : [parsed];
  return list.slice(0, MAX_UNITY_PROCESSES).map((item) => ({
    pid: Number(item?.ProcessId) || 0,
    creationDate: item?.CreationDate === undefined || item.CreationDate === null ? null : String(item.CreationDate),
    commandLine: typeof item?.CommandLine === "string" ? item.CommandLine : null
  }));
}

function withTimeout(promise, timeoutMs) {
  return new Promise((finish) => {
    const timer = setTimeout(() => finish({ spawnFailed: true, error: `timed out after ${timeoutMs}ms`, stdout: "", stderr: "" }), timeoutMs);
    promise.then((value) => { clearTimeout(timer); finish(value); });
  });
}

/** 标记目标进程是否属于 Dev 沙盒；正式项目 Editor 只记录，不改动。 */
function classifyUnityProcesses(processes, roots) {
  const devProject = DEV_PROJECT_DIR.toLowerCase();
  const devRoot = roots.devRoot.toLowerCase();
  return processes.map((entry) => {
    const commandLine = (entry.commandLine ?? "").toLowerCase();
    return {
      ...entry,
      isDevProjectEditor: commandLine.includes(devProject),
      mentionsDevDataRoot: commandLine.includes(devRoot)
    };
  });
}

/**
 * 生成子 CLI 环境：只在本进程派生的子进程内注入 UNITYCLI_DATA_DIR。
 * 不调用 setx，不修改用户级或系统级环境变量。
 */
function childEnvironment(roots, record) {
  const env = { ...process.env, UNITYCLI_DATA_DIR: roots.devRoot };
  const removedEnvKeys = [];
  if (env.UNITY_CLI_BRIDGE_ENDPOINT !== undefined) {
    delete env.UNITY_CLI_BRIDGE_ENDPOINT;
    removedEnvKeys.push("UNITY_CLI_BRIDGE_ENDPOINT");
  }
  record.inheritedDataDir = process.env.UNITYCLI_DATA_DIR ?? null;
  record.removedEnvKeys = removedEnvKeys;
  return env;
}

/** 派生公开 CLI：入口固定为仓库内 dist/cli.js，cwd 固定在仓库根，超时后终止整棵进程树。 */
async function runCli(argv, options) {
  const env = childEnvironment(options.roots, options.record);
  const startedAtMs = Date.now();
  const child = spawn(process.execPath, [CLI_ENTRY, ...argv], {
    shell: false,
    windowsHide: true,
    cwd: REPO_ROOT,
    env,
    stdio: ["ignore", "pipe", "pipe"]
  });
  let stdout = "";
  let stderr = "";
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    if (process.platform === "win32" && child.pid) spawn("taskkill", ["/pid", String(child.pid), "/t", "/f"], { windowsHide: true, stdio: "ignore" });
    else child.kill("SIGKILL");
  }, options.timeoutMs);
  child.stdout.on("data", (chunk) => { stdout += chunk; });
  child.stderr.on("data", (chunk) => { stderr += chunk; });
  const exitCode = await new Promise((finish) => {
    child.on("error", () => finish(null));
    child.on("close", (code) => finish(code));
  });
  clearTimeout(timer);
  return { argv, exitCode, stdout, stderr, timedOut, durationMs: Date.now() - startedAtMs };
}

function parseEnvelope(stdout) {
  const text = stdout.trim();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

// ── 子命令计划（dry-run 只打印，不执行、不写入、不读取） ───────────────────
function plannedIsolationWrite(roots, writeFile, extraReads = []) {
  return {
    plannedWrites: [writeFile],
    plannedDeletes: [],
    plannedReads: [roots.releaseRoot, roots.devRoot, ...extraReads],
    plannedCommands: [],
    notes: [
      "正式根只读盘点，不新增或删除正式根任何文件。",
      "盘点结果写入 Dev 临时区的固定文件，每次运行覆盖同一路径。"
    ]
  };
}

function plannedInstallFixture() {
  return {
    plannedWrites: [DEV_FIXTURE_DIR],
    plannedDeletes: [DEV_FIXTURE_DIR],
    plannedReads: [FIXTURE_SOURCE_DIR, DEV_PROJECT_VERSION_FILE],
    plannedCommands: [],
    notes: [
      `先删除固定的目标目录 ${DEV_FIXTURE_DIR}，再从 ${FIXTURE_SOURCE_DIR} 递归复制，保证可重复运行且不累积。`,
      "目标目录严格位于 Dev 项目的 Assets 下，不触碰仓库外或用户真实项目。",
      "复制后需要 Dev Unity Editor 重新编译脚本，本脚本不启动 Editor。"
    ]
  };
}

function plannedSyncPackage(closeEditor, roots) {
  const argv = ["init", "package", "--project", DEV_PROJECT_DIR, ...(closeEditor ? ["--close-editor"] : [])];
  return {
    argv,
    plannedWrites: [
      resolve(DEV_PROJECT_DIR, "Packages", "manifest.json"),
      resolve(DEV_PROJECT_DIR, "Packages", "packages-lock.json"),
      resolve(DEV_PROJECT_DIR, "Packages", "unitycli.install.json")
    ],
    plannedDeletes: [],
    plannedReads: [CLI_ENTRY],
    plannedCommands: [[process.execPath, CLI_ENTRY, ...argv]],
    plannedEnv: { UNITYCLI_DATA_DIR: roots.devRoot },
    notes: [
      "UNITYCLI_DATA_DIR 只注入本脚本派生的子 CLI 进程，不写用户级或系统级环境变量。",
      closeEditor
        ? "已显式请求 --close-editor：CLI 只会正常关闭 Dev 项目的 Editor；遇到未保存或 modal 阻断必须停止。"
        : "未请求 --close-editor：Dev 项目正在运行时 CLI 预期以 E_INIT_EDITOR_RUNNING 失败，本脚本不会替你关闭 Editor。"
    ]
  };
}

function plannedState(roots) {
  const argv = ["tools", "run", "editor.state", "--input", "{}", "--instance", DEV_PROJECT_DIR, "--timeout-ms", String(STATE_TIMEOUT_MS)];
  return {
    argv,
    plannedWrites: [],
    plannedDeletes: [],
    plannedReads: [DEV_PROJECT_DIR],
    plannedCommands: [[process.execPath, CLI_ENTRY, ...argv]],
    plannedEnv: { UNITYCLI_DATA_DIR: roots.devRoot },
    notes: ["只读调用公开 CLI 读取 Dev Editor 状态；不启动、不关闭任何 Editor。"]
  };
}

// ── 子命令实现 ─────────────────────────────────────────────────────────────
async function isolationBefore(roots) {
  const releaseRoot = await inventoryRoot(roots.releaseRoot);
  const devRoot = await inventoryRoot(roots.devRoot);
  const unityProcesses = classifyUnityProcesses(await listUnityProcesses(), roots);
  const record = {
    recordedAt: new Date().toISOString(),
    releaseDataRoot: releaseRoot,
    devDataRoot: devRoot,
    unityProcesses
  };
  await mkdir(roots.devTempDir, { recursive: true });
  await writeFile(roots.baselineFile, `${JSON.stringify(record, null, 2)}\n`, "utf8");
  return {
    baselineFile: roots.baselineFile,
    recordedAt: record.recordedAt,
    releaseDataRoot: inventorySummary(releaseRoot),
    devDataRoot: inventorySummary(devRoot),
    unityProcesses,
    devProjectEditorPids: unityProcesses.filter((entry) => entry.isDevProjectEditor).map((entry) => entry.pid)
  };
}

/** 安装夹具：固定目标目录整体重建，保证幂等且目标内不残留上一次的文件。 */
async function installFixture() {
  if (!await exists(FIXTURE_SOURCE_DIR)) {
    throw new HarnessError("E_HARNESS_FIXTURE_SOURCE_MISSING", `Tracked fixture sources are missing: ${FIXTURE_SOURCE_DIR}`, [
      "Restore scripts/live-matrix/fixtures/debug-fixture from the repository."
    ]);
  }
  if (!await exists(DEV_PROJECT_VERSION_FILE)) {
    throw new HarnessError("E_HARNESS_DEV_PROJECT_MISSING", `The Dev Unity project is missing: ${DEV_PROJECT_VERSION_FILE}`, [
      "Create or restore the Dev sandbox project at Dev/UnityProject before installing the fixture."
    ]);
  }
  if (DEV_FIXTURE_DIR === DEV_ASSETS_DIR || !DEV_FIXTURE_DIR.startsWith(`${DEV_ASSETS_DIR}${sep}`)) {
    throw new HarnessError("E_HARNESS_TARGET_OUTSIDE_DEV", `The fixture target resolved outside the Dev project Assets tree: ${DEV_FIXTURE_DIR}`, [
      "Fix the harness constants; the fixture must stay under Dev/UnityProject/Assets."
    ]);
  }
  const sourceFiles = await inventoryRoot(FIXTURE_SOURCE_DIR);
  await rm(DEV_FIXTURE_DIR, { recursive: true, force: true });
  // fs.cp 把源目录内容写进目标目录；目标目录由它自己创建，源内的 Editor/ 子目录结构原样保留。
  await cp(FIXTURE_SOURCE_DIR, DEV_FIXTURE_DIR, { recursive: true });
  const installed = await inventoryRoot(DEV_FIXTURE_DIR);
  return {
    sourceDir: FIXTURE_SOURCE_DIR,
    targetDir: DEV_FIXTURE_DIR,
    sourceFileCount: sourceFiles.files.length,
    installedFileCount: installed.files.length,
    installedFiles: installed.files.map((file) => file.path)
  };
}

async function syncPackage(roots, closeEditor) {
  const plan = plannedSyncPackage(closeEditor, roots);
  const record = {};
  const result = await runCli(["init", "package", "--project", DEV_PROJECT_DIR, ...(closeEditor ? ["--close-editor"] : [])], {
    roots,
    timeoutMs: CLI_TIMEOUT_MS,
    record
  });
  const envelope = parseEnvelope(result.stdout);
  const data = {
    argv: result.argv,
    dataDir: roots.devRoot,
    closeEditor,
    inheritedDataDir: record.inheritedDataDir,
    removedEnvKeys: record.removedEnvKeys,
    exitCode: result.exitCode,
    timedOut: result.timedOut,
    durationMs: result.durationMs,
    envelope,
    stderr: result.stderr.trim()
  };
  if (result.timedOut || result.exitCode !== 0 || envelope?.ok !== true) {
    throw new HarnessError("E_HARNESS_CLI_FAILED", "init package did not return a successful envelope for the Dev project.", [
      "Inspect data.envelope and data.stderr, resolve the reported external state, then rerun sync-package.",
      ...(closeEditor ? [] : ["If the Dev Editor is running and you want it closed normally, rerun with --close-editor."])
    ], { plan: { argv: plan.argv, plannedEnv: plan.plannedEnv }, ...data });
  }
  return data;
}

async function readState(roots) {
  const plan = plannedState(roots);
  const record = {};
  const result = await runCli(plan.argv, { roots, timeoutMs: CLI_TIMEOUT_MS, record });
  const envelope = parseEnvelope(result.stdout);
  const data = {
    argv: result.argv,
    dataDir: roots.devRoot,
    inheritedDataDir: record.inheritedDataDir,
    removedEnvKeys: record.removedEnvKeys,
    exitCode: result.exitCode,
    timedOut: result.timedOut,
    durationMs: result.durationMs,
    envelope,
    stderr: result.stderr.trim()
  };
  if (result.timedOut || result.exitCode !== 0 || envelope?.ok !== true) {
    throw new HarnessError("E_HARNESS_CLI_FAILED", "editor.state did not return a successful envelope for the Dev project.", [
      "Confirm the Dev Editor is running and not busy, then rerun state.",
      "Inspect data.envelope and data.stderr for the exact CLI error code."
    ], data);
  }
  return data;
}

/**
 * 隔离核验：正式根文件集合必须完全不变，Dev 记录只允许出现在 Dev 根。
 * 内容/时间差异只报告不判失败，因为其他进程（例如用户正在使用的正式 Editor 心跳）可以合法重写正式根文件。
 */
async function isolationAfter(roots) {
  let baseline;
  try {
    baseline = JSON.parse(await readFile(roots.baselineFile, "utf8"));
  } catch (error) {
    throw new HarnessError("E_HARNESS_BASELINE_MISSING", `Isolation baseline could not be read: ${roots.baselineFile} (${String(error)})`, [
      "Run the isolation-before subcommand before any Dev Unity work."
    ]);
  }
  const releaseRoot = await inventoryRoot(roots.releaseRoot);
  const devRoot = await inventoryRoot(roots.devRoot);
  const unityProcesses = classifyUnityProcesses(await listUnityProcesses(), roots);

  const baselineRelease = baseline.releaseDataRoot ?? { exists: false, files: [], fileSetHash: null };
  const baselinePaths = new Set((baselineRelease.files ?? []).map((file) => file.path));
  const currentPaths = new Set(releaseRoot.files.map((file) => file.path));
  const addedPaths = [...currentPaths].filter((path) => !baselinePaths.has(path)).sort();
  const removedPaths = [...baselinePaths].filter((path) => !currentPaths.has(path)).sort();
  const baselineSizes = new Map((baselineRelease.files ?? []).map((file) => [file.path, file.size]));
  const baselineTimes = new Map((baselineRelease.files ?? []).map((file) => [file.path, file.modifiedMs]));
  const sizeChangedPaths = releaseRoot.files.filter((file) => baselineSizes.has(file.path) && baselineSizes.get(file.path) !== file.size).map((file) => file.path).sort();
  const modifiedTimeChangedPaths = releaseRoot.files.filter((file) => baselineTimes.has(file.path) && baselineTimes.get(file.path) !== file.modifiedMs).map((file) => file.path).sort();

  const devPathsLeakedIntoReleaseRoot = [...currentPaths].filter((path) => path.toLowerCase().includes("unitycli-dev")).sort();
  const rootsOverlap = roots.releaseRoot === roots.devRoot || roots.devRoot.toLowerCase().startsWith(`${roots.releaseRoot.toLowerCase()}${sep}`);
  const baselinePids = new Set((baseline.unityProcesses ?? []).map((entry) => entry.pid));
  const currentPids = new Set(unityProcesses.map((entry) => entry.pid));
  const addedProcessPids = [...currentPids].filter((pid) => !baselinePids.has(pid)).sort((left, right) => left - right);
  const removedProcessPids = [...baselinePids].filter((pid) => !currentPids.has(pid)).sort((left, right) => left - right);
  const devRootHasRecords = devRoot.exists && devRoot.files.length > 0;

  const failures = [];
  if (rootsOverlap) failures.push("The Dev data root overlaps the release data root, so isolation cannot be proven.");
  if (releaseRoot.exists !== baselineRelease.exists) failures.push("The release data root existence changed since the baseline.");
  if (addedPaths.length > 0) failures.push(`Files appeared in the release data root: ${addedPaths.join(", ")}`);
  if (removedPaths.length > 0) failures.push(`Files disappeared from the release data root: ${removedPaths.join(", ")}`);
  if (releaseRoot.truncated || baselineRelease.truncated === true) failures.push("The release data root inventory hit its entry bound, so equality cannot be proven.");
  if (devPathsLeakedIntoReleaseRoot.length > 0) failures.push(`Dev-root records were found under the release data root: ${devPathsLeakedIntoReleaseRoot.join(", ")}`);
  if (!devRootHasRecords) failures.push(`No Dev records were found under the Dev data root: ${roots.devRoot}`);

  const data = {
    baselineFile: roots.baselineFile,
    afterFile: roots.afterFile,
    recordedAt: new Date().toISOString(),
    baselineRecordedAt: baseline.recordedAt ?? null,
    releaseRootUnchanged: addedPaths.length === 0 && removedPaths.length === 0 && releaseRoot.exists === baselineRelease.exists && !releaseRoot.truncated,
    addedPaths,
    removedPaths,
    sizeChangedPaths,
    modifiedTimeChangedPaths,
    devPathsLeakedIntoReleaseRoot,
    devDataRoot: inventorySummary(devRoot),
    devDataRootHasRecords: devRootHasRecords,
    unityProcesses,
    processDelta: { addedProcessPids, removedProcessPids },
    failures
  };
  await mkdir(roots.devTempDir, { recursive: true });
  await writeFile(roots.afterFile, `${JSON.stringify(data, null, 2)}\n`, "utf8");
  if (failures.length > 0) {
    throw new HarnessError("E_HARNESS_ISOLATION_BREACH", "Dev verification broke data-root isolation.", [
      "Inspect data.addedPaths and data.removedPaths, restore the release data root to its baseline, then rerun isolation-before followed by the Dev work.",
      "Do not delete or edit release-root files to silence this check without identifying which process wrote them."
    ], data);
  }
  return data;
}

// ── 入口与输出 ─────────────────────────────────────────────────────────────
function parseArguments(argv) {
  const command = argv[0];
  if (!command || !COMMANDS.has(command)) {
    throw new HarnessError("E_HARNESS_UNKNOWN_COMMAND", `Unknown or missing subcommand: ${command ?? "<none>"}`, [
      `Supported subcommands: ${[...COMMANDS].join(", ")}.`,
      `Usage: ${USAGE}`
    ]);
  }
  let dryRun = false;
  let closeEditor = false;
  for (let index = 1; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--dry-run") { dryRun = true; continue; }
    if (token === "--close-editor") { closeEditor = true; continue; }
    throw new HarnessError("E_HARNESS_UNKNOWN_OPTION", `Unknown option for ${command}: ${token}`, [
      "Supported options are --dry-run and, for sync-package only, --close-editor."
    ]);
  }
  if (closeEditor && command !== "sync-package") {
    throw new HarnessError("E_HARNESS_UNKNOWN_OPTION", `--close-editor is only valid for sync-package, not for ${command}.`, [
      "Rerun without --close-editor; this harness never closes an Editor for any other subcommand."
    ]);
  }
  return { command, dryRun, closeEditor };
}

function emit(envelope, exitCode) {
  process.stdout.write(`${JSON.stringify(envelope)}\n`);
  process.exitCode = exitCode;
}

async function main() {
  const { command, dryRun, closeEditor } = parseArguments(process.argv.slice(2));
  const roots = dataRoots();

  if (dryRun) {
    const plan = command === "install-fixture" ? plannedInstallFixture()
      : command === "sync-package" ? plannedSyncPackage(closeEditor, roots)
        : command === "state" ? plannedState(roots)
          : plannedIsolationWrite(roots, command === "isolation-before" ? roots.baselineFile : roots.afterFile, command === "isolation-after" ? [roots.baselineFile] : []);
    emit({ ok: true, command, dryRun: true, data: { ...plan, fixtureSourceDir: FIXTURE_SOURCE_DIR, devProjectDir: DEV_PROJECT_DIR, cliEntry: CLI_ENTRY } }, 0);
    return;
  }

  let data;
  if (command === "isolation-before") data = await isolationBefore(roots);
  else if (command === "install-fixture") data = await installFixture();
  else if (command === "sync-package") data = await syncPackage(roots, closeEditor);
  else if (command === "state") data = await readState(roots);
  else data = await isolationAfter(roots);
  emit({ ok: true, command, dryRun: false, data }, 0);
}

main().catch((error) => {
  if (error instanceof HarnessError) {
    emit({ ok: false, command: process.argv[2] ?? null, error: { code: error.code, message: error.message, nextActions: error.nextActions, ...(error.data === undefined ? {} : { details: error.data }) } }, 1);
    return;
  }
  emit({
    ok: false,
    command: process.argv[2] ?? null,
    error: {
      code: "E_HARNESS_UNEXPECTED",
      message: error instanceof Error ? error.message : String(error),
      nextActions: [`Rerun the harness; if it repeats, inspect the stack trace below and fix the harness.`, String(error instanceof Error ? error.stack : error)]
    }
  }, 1);
});
