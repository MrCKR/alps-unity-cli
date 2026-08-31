import { lstat, readFile, readdir, realpath, stat } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { homedir } from "node:os";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import { findSkillSourcePath, inspectSkillTarget, resolveSkillTarget, type AgentKind } from "./skill-installer.js";

const maxDescendantDepth = 4;
const maxScannedDirectories = 2000;
const maxUnityProjects = 100;
const maxWorkspaceCandidates = 20;
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
    add(await unityProjectCandidate(ancestor, depth === 0 ? "cwd" : "ancestor", depth));
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
function workspaceCandidates(cwd: string, repositoryRoot: string | undefined, projects: UnityProjectCandidate[]): Array<{ path: string; reason: string }> {
  const values: Array<{ path: string; reason: string }> = [{ path: cwd, reason: "current-working-directory" }];
  if (repositoryRoot && pathIdentity(repositoryRoot) !== pathIdentity(cwd)) values.push({ path: repositoryRoot, reason: "repository-root" });
  for (const project of projects) values.push({ path: dirname(project.path), reason: "unity-project-parent" });
  const seen = new Set<string>();
  return values.filter((item) => { const id = pathIdentity(item.path); if (seen.has(id) || seen.size >= maxWorkspaceCandidates) return false; seen.add(id); return true; });
}

export async function discoverInitEnvironment(cwdInput: string, userHomeInput: string): Promise<Record<string, unknown>> {
  const cwd = await realpath(resolve(cwdInput));
  const userHome = await realpath(resolve(userHomeInput));
  const repositoryRoot = await findRepositoryRoot(cwd);
  const discovery = await discoverUnityProjects(cwd);
  const workspaces = workspaceCandidates(cwd, repositoryRoot, discovery.projects);
  const sourcePath = await findSkillSourcePath();
  const copies: Array<Record<string, unknown>> = [];
  for (const workspace of workspaces) for (const agent of [".agents", "claude"] as AgentKind[]) {
    const targetPath = resolveSkillTarget(workspace.path, agent);
    copies.push({ scope: "workspace", workspace: workspace.path, agent, ...await inspectSkillTarget(sourcePath, targetPath) });
  }
  for (const agent of [".agents", "claude"] as AgentKind[]) {
    const targetPath = resolveSkillTarget(userHome, agent);
    copies.push({ scope: "user", agent, ...await inspectSkillTarget(sourcePath, targetPath) });
  }
  const shadowingRisks = ([".agents", "claude"] as AgentKind[]).flatMap((agent) => {
    const matches = copies.filter((copy) => copy.agent === agent && copy.status !== "missing");
    if (matches.length < 2) return [];
    return [{
      skill: "unitycli",
      agent,
      precedence: agent === "claude" ? ["user", "workspace"] : ["workspace", "user"],
      copies: matches,
      note: agent === "claude" ? "Claude personal skills override workspace skills." : "Generic Agent Skills commonly prefer workspace skills over user skills.",
    }];
  });
  return {
    action: "init_discover", cwd, repositoryRoot: repositoryRoot ?? null,
    limits: { maxDescendantDepth, maxScannedDirectories, maxUnityProjects, maxWorkspaceCandidates },
    scan: { scannedDirectories: discovery.scannedDirectories, truncated: discovery.truncated },
    unityProjects: discovery.projects, skillWorkspaces: workspaces, skillCopies: copies,
    shadowingRisks,
    requiredDecisions: [
      { id: "bridgeProjects", question: "Which discovered Unity projects should receive the UnityCLI Bridge?", choices: discovery.projects.map((project) => project.path), multiple: true },
      { id: "skillAgent", question: "Which Skill host should be configured?", choices: [".agents", "claude"], multiple: false },
      { id: "skillScope", question: "Should the Skill be installed for one workspace or for the current user?", choices: ["workspace", "user"], multiple: false },
      { id: "skillWorkspace", question: "If workspace scope is selected, which directory is the user's normal AI working directory?", choices: workspaces.map((workspace) => workspace.path), multiple: false },
    ],
    nextActions: ["Ask the user every required decision before running any installation effect.", "Use the exact command templates returned by `unitycli init help` after the user answers."],
  };
}

/** 从当前工作目录有界发现安装现场，不修改文件或进程。 */
export async function handleInitDiscover(args: string[], flags: Record<string, string | boolean>, requestId: string): Promise<CliResult> {
  if (args.length) return failure(requestId, "E_INIT_ARGUMENT_UNEXPECTED", "init discover does not accept positional arguments.", ["Run `unitycli init discover` without additional arguments."]);
  const unknown = Object.keys(flags).find((name) => name !== "pretty");
  if (unknown) return failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for init discover: --${unknown}.`, ["Run `unitycli init discover` without options."], { option: `--${unknown}` });
  try { return success(requestId, await discoverInitEnvironment(process.cwd(), homedir())); }
  catch (error) { return failure(requestId, "INIT_DISCOVERY_FAILED", error instanceof Error ? error.message : String(error), ["Run `unitycli init discover` from an existing readable working directory."]); }
}
