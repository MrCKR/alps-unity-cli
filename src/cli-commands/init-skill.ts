import { lstat, realpath, rm } from "node:fs/promises";
import { homedir } from "node:os";
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import { agentSkillInstaller, findSkillSourcePath, inspectSkillTarget, installSkillTarget, parseAgentList, resolveSkillTarget, SkillInstallerError, type AgentKind } from "./skill-installer.js";
import type { Runtime } from "./types.js";

export type SkillScope = "workspace" | "user";

/** 受管理目录移除旧投影时提示用户：自有内容应放在 user/。 */
function skillRemovalWarnings(changes: { removedFiles?: string[] }[]): string[] {
  const removed = changes.flatMap((change) => change.removedFiles ?? []);
  if (removed.length === 0) return [];
  const sample = removed.slice(0, 5).join(", ");
  return [`Removed ${removed.length} managed Skill file(s) no longer projected: ${sample}. Keep custom Skill content under user/ (entry user/ROUTING.md).`];
}

export function resolveInitSkillTarget(agent: AgentKind, scope: SkillScope, workspace: string | undefined, userHome: string): { basePath: string; targetPath: string } {
  const basePath = scope === "user" ? resolve(userHome) : resolve(workspace!);
  return { basePath, targetPath: resolveSkillTarget(basePath, agent) };
}

async function assertRealDirectory(path: string, label: string): Promise<string> {
  try {
    const info = await lstat(path);
    if (!info.isDirectory() || info.isSymbolicLink()) throw new Error("not a real directory");
    return await realpath(path);
  } catch {
    throw new SkillInstallerError("E_SKILL_ROOT_INVALID", `${label} must be an existing real directory: ${path}`, [`Pass an existing non-linked ${label.toLowerCase()} path.`]);
  }
}

async function rejectLinkedTargetAncestors(basePath: string, agent: AgentKind): Promise<void> {
  const agentRoot = join(basePath, agent === ".agents" ? ".agents" : ".claude");
  for (const path of [agentRoot, join(agentRoot, "skills"), join(agentRoot, "skills", "unitycli")]) {
    try {
      const info = await lstat(path);
      if (info.isSymbolicLink() || !info.isDirectory()) throw new SkillInstallerError("E_SKILL_TARGET_UNSAFE", `Skill target ancestor must be a real directory: ${path}`, ["Replace the linked or non-directory target ancestor before retrying."]);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") continue;
      throw error;
    }
  }
}

function timeout(flags: Record<string, string | boolean>): number | undefined {
  const raw = getString(flags, "timeout-ms");
  if (raw === undefined) return 60_000;
  const value = Number(raw);
  return Number.isSafeInteger(value) && value > 0 ? value : undefined;
}

/** 明确目标允许尚未创建，但所有既有祖先必须是真实目录且入口名固定为unitycli。 */
async function resolveExplicitSkillTarget(raw: string): Promise<string> {
  const targetPath = resolve(raw);
  if (basename(targetPath).toLowerCase() !== "unitycli") {
    throw new SkillInstallerError("E_INIT_SKILL_TARGET_INVALID", "init skill --target must name the exact unitycli Skill directory.", ["Pass a path ending in the `unitycli` directory, such as `.pi/skills/unitycli`."]);
  }
  if (process.platform === "win32" && !/^[a-z]:\\/i.test(targetPath)) {
    throw new SkillInstallerError("E_INIT_SKILL_TARGET_INVALID", "init skill --target must resolve to a fixed local Windows path.", ["Use a local drive path for the exact UnityCLI Skill directory."]);
  }
  if (targetPath.split(/[\\/]/).some((segment) => segment === ".unitycli-install")) {
    throw new SkillInstallerError("E_INIT_SKILL_TARGET_INVALID", "init skill --target cannot be inside the installer transaction directory.", ["Choose the final UnityCLI Skill directory outside `.unitycli-install`."]);
  }
  let current = targetPath;
  while (true) {
    try {
      const info = await lstat(current);
      if (info.isSymbolicLink() || !info.isDirectory()) {
        throw new SkillInstallerError("E_SKILL_TARGET_UNSAFE", `Skill target ancestors must be real directories: ${current}`, ["Replace the linked or non-directory target ancestor before retrying."]);
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }
  return targetPath;
}

function pathContains(parent: string, child: string): boolean {
  const fromParent = relative(parent, child);
  return fromParent === "" || (!fromParent.startsWith("..") && !isAbsolute(fromParent));
}

/** bundled source与target不得互相包含，避免安装事务改写自己的准源。 */
function rejectSkillSourceOverlap(sourcePath: string, targetPath: string): void {
  const sourceRoot = resolve(dirname(sourcePath));
  const targetRoot = resolve(targetPath);
  const normalize = (value: string): string => process.platform === "win32" ? value.toLowerCase() : value;
  const sourceIdentity = normalize(sourceRoot);
  const targetIdentity = normalize(targetRoot);
  if (pathContains(sourceIdentity, targetIdentity) || pathContains(targetIdentity, sourceIdentity)) {
    throw new SkillInstallerError("E_SKILL_TARGET_OVERLAPS_SOURCE", "The selected Skill target overlaps the bundled UnityCLI Skill source.", ["Choose a separate installed Skill directory."]);
  }
}

/** 安装或只读检查一个预设agent目标或用户明确给出的精确Skill目录。 */
export async function handleInitSkill(args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (args.length) return failure(requestId, "E_INIT_ARGUMENT_UNEXPECTED", "init skill does not accept positional arguments.", ["Run `unitycli init skill` using only documented options."]);
  const agentRaw = getString(flags, "agent");
  const scopeRaw = getString(flags, "scope");
  const workspaceRaw = getString(flags, "workspace");
  const explicitTargetRaw = getString(flags, "target");
  if (explicitTargetRaw && (agentRaw || scopeRaw || workspaceRaw)) {
    return failure(requestId, "E_INIT_SKILL_TARGET_CONFLICT", "init skill --target cannot be combined with --agent, --scope, or --workspace.", ["Use either one exact --target directory or one preset agent/scope target."], { option: "--target" });
  }
  if (!explicitTargetRaw && !agentRaw) return failure(requestId, "E_INIT_SKILL_TARGET_REQUIRED", "init skill requires either --target <SkillDirectory> or --agent <.agents|claude> with --scope.", ["Use the user's explicit Skill directory when provided; otherwise choose one preset host."], { options: ["--target", "--agent"] });
  const timeoutMs = timeout(flags);
  if (timeoutMs === undefined) return failure(requestId, "E_INIT_TIMEOUT_INVALID", "init skill --timeout-ms must be a positive integer.", ["Pass --timeout-ms <positive-integer>."], { option: "--timeout-ms" });
  const deadlineMs = Date.now() + timeoutMs;

  try {
    const sourcePath = await findSkillSourcePath();
    if (explicitTargetRaw) {
      const targetPath = await resolveExplicitSkillTarget(explicitTargetRaw);
      rejectSkillSourceOverlap(sourcePath, targetPath);
      const installCommand = `unitycli init skill --target ${JSON.stringify(targetPath)}`;
      if (getBoolean(flags, "check")) {
        const inspection = await inspectSkillTarget(sourcePath, targetPath);
        return success(requestId, { action: "init_skill_check", target: targetPath, skill: inspection, nextActions: inspection.status === "installed" ? [] : [installCommand] });
      }
      const installRoot = join(dirname(targetPath), ".unitycli-install");
      const change = await installSkillTarget(sourcePath, targetPath, installRoot, deadlineMs);
      const inspection = await inspectSkillTarget(sourcePath, targetPath);
      if (inspection.status !== "installed") throw new SkillInstallerError("E_SKILL_INSTALL_PROJECTION_INVALID", "Skill installation did not reach an installed readback state.", ["Inspect the reported target before retrying."]);
      await rm(installRoot, { recursive: true, force: true });
      const warnings = skillRemovalWarnings([change]);
      return success(requestId, { action: "init_skill", target: targetPath, changes: [change], skill: inspection, nextActions: [`${installCommand} --check`] }, warnings.length > 0 ? { warnings } : undefined);
    }

    let agent: AgentKind;
    try {
      const agents = parseAgentList(agentRaw!);
      if (agents.length !== 1) throw new SkillInstallerError("E_INIT_AGENT_INVALID", "init skill accepts exactly one preset agent target.", ["Pass either --agent .agents or --agent claude."]);
      agent = agents[0];
    } catch (error) {
      if (error instanceof SkillInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
      throw error;
    }
    if (scopeRaw !== "workspace" && scopeRaw !== "user") return failure(requestId, "E_INIT_SCOPE_INVALID", "Preset Skill installation requires --scope <workspace|user>.", ["Choose workspace for the current project or user for the current Windows account."], { option: "--scope", actual: scopeRaw ?? null });
    const scope = scopeRaw as SkillScope;
    if (scope === "workspace" && !workspaceRaw) return failure(requestId, "E_INIT_WORKSPACE_REQUIRED", "Workspace Skill scope requires --workspace <path>.", ["Pass the user's normal AI working directory."], { option: "--workspace" });
    if (scope === "user" && workspaceRaw) return failure(requestId, "E_INIT_WORKSPACE_FORBIDDEN", "User Skill scope does not accept --workspace.", ["Remove --workspace or choose --scope workspace."], { option: "--workspace" });
    const baseCandidate = scope === "user" ? resolve(runtime.userHome ?? homedir()) : resolve(workspaceRaw!);
    const basePath = await assertRealDirectory(baseCandidate, scope === "user" ? "User home" : "Workspace");
    await rejectLinkedTargetAncestors(basePath, agent);
    const targetPath = resolveSkillTarget(basePath, agent);
    if (getBoolean(flags, "check")) {
      const inspection = await inspectSkillTarget(sourcePath, targetPath);
      const installCommand = scope === "workspace"
        ? `unitycli init skill --agent ${agent} --scope workspace --workspace ${JSON.stringify(basePath)}`
        : `unitycli init skill --agent ${agent} --scope user`;
      return success(requestId, { action: "init_skill_check", agent, scope, ...(scope === "workspace" ? { workspace: basePath } : {}), skill: inspection, nextActions: inspection.status === "installed" ? [] : [installCommand] });
    }
    const installRoot = scope === "user"
      ? join(runtime.dataDir, ".temp", "skill-install", agent === ".agents" ? "agents" : agent)
      : join(basePath, agent === ".agents" ? ".agents" : ".claude", "skills", ".unitycli-install");
    const changes = await agentSkillInstaller.installForAgents([agent], sourcePath, basePath, deadlineMs, installRoot);
    const inspection = await inspectSkillTarget(sourcePath, targetPath);
    if (inspection.status !== "installed") throw new SkillInstallerError("E_SKILL_INSTALL_PROJECTION_INVALID", "Skill installation did not reach an installed readback state.", ["Inspect the reported target before retrying."]);
    await rm(installRoot, { recursive: true, force: true });
    const warnings = skillRemovalWarnings(changes);
    return success(requestId, { action: "init_skill", agent, scope, ...(scope === "workspace" ? { workspace: basePath } : {}), changes, skill: inspection, nextActions: ["Run the same init skill command with --check."] }, warnings.length > 0 ? { warnings } : undefined);
  } catch (error) {
    if (error instanceof SkillInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
    return failure(requestId, "INIT_SKILL_FAILED", error instanceof Error ? error.message : String(error), ["Inspect the Skill source and selected target before retrying."]);
  }
}
