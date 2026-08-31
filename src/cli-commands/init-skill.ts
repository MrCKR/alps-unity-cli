import { lstat, realpath, rm } from "node:fs/promises";
import { homedir } from "node:os";
import { join, resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import { agentSkillInstaller, findSkillSourcePath, inspectSkillTarget, parseAgentList, resolveSkillTarget, SkillInstallerError, type AgentKind } from "./skill-installer.js";
import type { Runtime } from "./types.js";

export type SkillScope = "workspace" | "user";

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

/** 安装或只读检查一个明确 agent/scope Skill target。 */
export async function handleInitSkill(args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (args.length) return failure(requestId, "E_INIT_ARGUMENT_UNEXPECTED", "init skill does not accept positional arguments.", ["Run `unitycli init skill` using only documented options."]);
  const agentRaw = getString(flags, "agent");
  const scopeRaw = getString(flags, "scope");
  if (!agentRaw) return failure(requestId, "E_INIT_AGENT_REQUIRED", "init skill requires --agent <.agents|claude>.", ["Choose the Skill host after asking the user."], { option: "--agent" });
  let agent: AgentKind;
  try {
    const agents = parseAgentList(agentRaw);
    if (agents.length !== 1) throw new SkillInstallerError("E_INIT_AGENT_INVALID", "init skill accepts exactly one agent target.", ["Pass either --agent .agents or --agent claude."]);
    agent = agents[0];
  } catch (error) {
    if (error instanceof SkillInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
    throw error;
  }
  if (scopeRaw !== "workspace" && scopeRaw !== "user") return failure(requestId, "E_INIT_SCOPE_INVALID", "init skill requires --scope <workspace|user>.", ["Ask the user whether this Skill belongs to one workspace or the current user."], { option: "--scope", actual: scopeRaw ?? null });
  const scope = scopeRaw as SkillScope;
  const workspaceRaw = getString(flags, "workspace");
  if (scope === "workspace" && !workspaceRaw) return failure(requestId, "E_INIT_WORKSPACE_REQUIRED", "Workspace Skill scope requires --workspace <path>.", ["Pass the user's normal AI working directory."], { option: "--workspace" });
  if (scope === "user" && workspaceRaw) return failure(requestId, "E_INIT_WORKSPACE_FORBIDDEN", "User Skill scope does not accept --workspace.", ["Remove --workspace or choose --scope workspace."], { option: "--workspace" });
  const timeoutMs = timeout(flags);
  if (timeoutMs === undefined) return failure(requestId, "E_INIT_TIMEOUT_INVALID", "init skill --timeout-ms must be a positive integer.", ["Pass --timeout-ms <positive-integer>."], { option: "--timeout-ms" });

  try {
    const baseCandidate = scope === "user" ? resolve(runtime.userHome ?? homedir()) : resolve(workspaceRaw!);
    const basePath = await assertRealDirectory(baseCandidate, scope === "user" ? "User home" : "Workspace");
    await rejectLinkedTargetAncestors(basePath, agent);
    const targetPath = resolveSkillTarget(basePath, agent);
    const sourcePath = await findSkillSourcePath();
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
    const deadlineMs = Date.now() + timeoutMs;
    const changes = await agentSkillInstaller.installForAgents([agent], sourcePath, basePath, deadlineMs, installRoot);
    const inspection = await inspectSkillTarget(sourcePath, targetPath);
    if (inspection.status !== "installed") throw new SkillInstallerError("E_SKILL_INSTALL_PROJECTION_INVALID", "Skill installation did not reach an installed readback state.", ["Inspect the reported target before retrying."]);
    await rm(installRoot, { recursive: true, force: true });
    return success(requestId, { action: "init_skill", agent, scope, ...(scope === "workspace" ? { workspace: basePath } : {}), changes, skill: inspection, nextActions: ["Run the same init skill command with --check."] });
  } catch (error) {
    if (error instanceof SkillInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
    return failure(requestId, "INIT_SKILL_FAILED", error instanceof Error ? error.message : String(error), ["Inspect the Skill source and selected target before retrying."]);
  }
}
