import { lstat, readFile, readdir, realpath, stat } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { homedir } from "node:os";
import { inspectPackageBridge, type PackageBridgeInspection } from "../package-manager.js";
import { resolveProjectIdentityAsync } from "../project-identity.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import { findUnityProjectProcesses } from "../unity-project-processes.js";
import { findSkillSourcePath, inspectSkillTarget, resolveSkillTarget } from "./skill-installer.js";
import type { Runtime } from "./types.js";

const maxDescendantDepth = 4;
const maxScannedDirectories = 2000;
const maxUnityProjects = 100;
const ignoredDirectories = new Set([".git", "node_modules", "library", "temp", "logs", "obj"]);

interface UnityProjectCandidate {
  path: string;
  unityVersion: string;
  relation: "cwd" | "ancestor" | "descendant";
  depth: number;
}

async function directoryExists(path: string): Promise<boolean> { try { return (await stat(path)).isDirectory(); } catch { return false; } }
async function readUnityVersion(path: string): Promise<string | undefined> {
  try { return /^m_EditorVersion:\s*(.+?)\s*$/m.exec(await readFile(join(path, "ProjectSettings", "ProjectVersion.txt"), "utf8"))?.[1]; }
  catch { return undefined; }
}
async function unityProjectCandidate(path: string, relation: UnityProjectCandidate["relation"], depth: number): Promise<UnityProjectCandidate | undefined> {
  try {
    const info = await lstat(path);
    if (!info.isDirectory() || info.isSymbolicLink()) return undefined;
    const [assets, manifest, unityVersion] = await Promise.all([
      directoryExists(join(path, "Assets")),
      readFile(join(path, "Packages", "manifest.json"), "utf8").then((text) => { JSON.parse(text); return true; }).catch(() => false),
      readUnityVersion(path),
    ]);
    return assets && manifest && unityVersion ? { path: await realpath(path), unityVersion, relation, depth } : undefined;
  } catch { return undefined; }
}
function pathIdentity(path: string): string { return process.platform === "win32" ? path.toLowerCase() : path; }
async function findRepositoryRoot(cwd: string): Promise<string | undefined> {
  let current = cwd;
  for (let depth = 0; depth < 64; depth += 1) {
    try { await lstat(join(current, ".git")); return current; } catch { /* continue */ }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }
  return undefined;
}
async function discoverUnityProjects(cwd: string): Promise<{ projects: UnityProjectCandidate[]; scannedDirectories: number; truncated: boolean }> {
  const projects: UnityProjectCandidate[] = [];
  const identities = new Set<string>();
  const add = (candidate: UnityProjectCandidate | undefined): void => {
    if (!candidate || projects.length >= maxUnityProjects || identities.has(pathIdentity(candidate.path))) return;
    identities.add(pathIdentity(candidate.path)); projects.push(candidate);
  };
  let ancestor = cwd;
  for (let depth = 0; depth < 64; depth += 1) {
    const candidate = await unityProjectCandidate(ancestor, depth === 0 ? "cwd" : "ancestor", depth);
    if (candidate) return { projects: [candidate], scannedDirectories: 0, truncated: false };
    const parent = dirname(ancestor); if (parent === ancestor) break; ancestor = parent;
  }
  const queue: Array<{ path: string; depth: number }> = [{ path: cwd, depth: 0 }];
  let scannedDirectories = 0;
  let truncated = false;
  for (let index = 0; index < queue.length; index += 1) {
    const item = queue[index];
    if (++scannedDirectories > maxScannedDirectories || projects.length >= maxUnityProjects) { truncated = true; break; }
    if (item.depth > 0) add(await unityProjectCandidate(item.path, "descendant", item.depth));
    if (item.depth >= maxDescendantDepth) continue;
    let entries;
    try { entries = await readdir(item.path, { withFileTypes: true }); } catch { continue; }
    for (const entry of entries.sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0)) {
      if (!entry.isDirectory() || entry.isSymbolicLink() || ignoredDirectories.has(entry.name.toLowerCase())) continue;
      if (queue.length >= maxScannedDirectories) { truncated = true; break; }
      queue.push({ path: join(item.path, entry.name), depth: item.depth + 1 });
    }
  }
  projects.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
  return { projects, scannedDirectories: Math.min(scannedDirectories, maxScannedDirectories), truncated };
}
export interface InitDiscoveryRuntime {
  findUnityProjectProcesses?: Runtime["findUnityProjectProcesses"];
  inspectPackageBridge?: (projectPath: string) => Promise<PackageBridgeInspection>;
}

/** 当前项目常见路径返回完整安装计划；多项目搜索只保留选择所需信息。 */
export async function discoverInitEnvironment(cwdInput: string, userHomeInput: string, runtime: InitDiscoveryRuntime = {}): Promise<Record<string, unknown>> {
  const cwd = await realpath(resolve(cwdInput));
  await realpath(resolve(userHomeInput));
  const repositoryRoot = await findRepositoryRoot(cwd);
  const discovery = await discoverUnityProjects(cwd);
  const inspectBridge = runtime.inspectPackageBridge ?? inspectPackageBridge;
  const findEditors = runtime.findUnityProjectProcesses ?? findUnityProjectProcesses;
  const inspectLiveState = discovery.projects.length === 1;
  const unityProjects: Array<Record<string, unknown>> = [];
  for (const project of discovery.projects) {
    let bridge: Record<string, unknown> = { status: "not-inspected" };
    let editor: Record<string, unknown> = { status: "not-inspected" };
    if (inspectLiveState) {
      try {
        const inspection = await inspectBridge(project.path);
        bridge = {
          status: inspection.status,
          availableVersion: inspection.availableVersion,
          ...(inspection.installedVersion ? { installedVersion: inspection.installedVersion } : {}),
          ...(inspection.reasonCode ? { reasonCode: inspection.reasonCode } : {}),
        };
      } catch {
        bridge = { status: "unverifiable", reasonCode: "INIT_BRIDGE_STATE_UNVERIFIABLE" };
      }
      try {
        const identity = await resolveProjectIdentityAsync(project.path, { timeoutMs: 3_000 });
        const processes = await findEditors(identity.instanceId, identity.canonicalProjectPath, 3_000);
        editor = { status: processes.length ? "running" : "stopped", processCount: processes.length };
      } catch {
        editor = { status: "unverifiable" };
      }
    }
    unityProjects.push({ ...project, bridge, editor });
  }

  const sourcePath = await findSkillSourcePath();
  const recommendedTarget = resolveSkillTarget(cwd, ".agents");
  const recommendedSkill = await inspectSkillTarget(sourcePath, recommendedTarget);
  const requiredDecisions: Array<Record<string, unknown>> = [];
  if (unityProjects.length > 1) {
    requiredDecisions.push({ id: "bridgeProjects", question: "Which discovered Unity projects should receive the UnityCLI Bridge?", choices: unityProjects.map((project) => project.path), multiple: true });
  } else if (((unityProjects[0]?.editor as Record<string, unknown> | undefined)?.status) === "running") {
    requiredDecisions.push({ id: "closeEditor", question: "May UnityCLI normally close the exact target Unity project before Bridge installation?", projectPath: unityProjects[0].path, multiple: false });
  }

  return {
    action: "init_discover",
    cwd,
    repositoryRoot: repositoryRoot ?? null,
    unityProjects,
    skill: { workspace: cwd, recommendedTarget, explicitTargetSupported: true, ...recommendedSkill },
    requiredDecisions,
    ...(discovery.scannedDirectories > 0 || discovery.truncated ? { scan: { scannedDirectories: discovery.scannedDirectories, truncated: discovery.truncated, maxDescendantDepth, maxScannedDirectories, maxUnityProjects } } : {}),
    nextActions: requiredDecisions.length
      ? ["Ask only the returned required decisions; keep any target the user already specified."]
      : ["Use the unique current project and the user's explicit Skill target, or the recommended workspace target when none was specified."],
  };
}

/** 从当前工作目录有界发现安装现场，不修改文件或进程。 */
export async function handleInitDiscover(args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (args.length) return failure(requestId, "E_INIT_ARGUMENT_UNEXPECTED", "init discover does not accept positional arguments.", ["Run `unitycli init discover` without additional arguments."]);
  const unknown = Object.keys(flags).find((name) => name !== "pretty");
  if (unknown) return failure(requestId, "E_SHELL_OPTION_UNKNOWN", `Unknown option for init discover: --${unknown}.`, ["Run `unitycli init discover` without options."], { option: `--${unknown}` });
  try { return success(requestId, await discoverInitEnvironment(process.cwd(), homedir(), { findUnityProjectProcesses: runtime.findUnityProjectProcesses })); }
  catch (error) { return failure(requestId, "INIT_DISCOVERY_FAILED", error instanceof Error ? error.message : String(error), ["Run `unitycli init discover` from an existing readable working directory."]); }
}
