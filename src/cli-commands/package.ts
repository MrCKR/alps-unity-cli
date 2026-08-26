import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { failure, success } from "../output.js";
import { ProjectIdentityError, resolveProjectIdentity } from "../project-identity.js";
import { StructuredToolError } from "../tools/errors.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";

/** 安装 Core package、Agent Skill 和自动检测 Extension，并只返回文件系统 readback。 */
export async function handleInit(flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (flags.unity !== undefined) {
    return failure(requestId, "E_OBSOLETE_OPTION", "init --unity has been removed; init only copies package and Skill files.", [
      "Remove --unity and run init against the Unity project path.",
      "Open or refresh that project in Unity after init completes."
    ]);
  }
  const allowedFlags = new Set(["project", "agent", "pkg-only", "skill-only", "check", "timeout-ms", "raw"]);
  const unknownFlag = Object.keys(flags).find((flag) => !allowedFlags.has(flag));
  if (unknownFlag) {
    return failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for init: --${unknownFlag}.`, ["Run unitycli --help for the supported init options."]);
  }

  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const { checkPackageBridge, ensurePackageBridge, preparePackageBridgeInstall, PackageManagerError } = await import("../package-manager.js");
  const { agentSkillInstaller, detectAgentKinds, findSkillSourcePath, parseAgentList, SkillInstallerError } = await import("./skill-installer.js");
  const agentFlag = getString(flags, "agent");
  const pkgOnly = getBoolean(flags, "pkg-only");
  const skillOnly = getBoolean(flags, "skill-only");
  const isCheck = getBoolean(flags, "check");
  let checkIdentity: ReturnType<typeof resolveProjectIdentity> | undefined;
  if (isCheck) {
    try {
      checkIdentity = resolveProjectIdentity(projectPath);
    } catch (error) {
      if (error instanceof ProjectIdentityError) return failure(requestId, error.code, error.message, ["Pass the root of an existing Unity project."], error.details);
      throw error;
    }
  }
  const agentDetectionPath = checkIdentity?.canonicalProjectPath ?? projectPath;
  const effectiveAgentFlag = agentFlag ?? (!pkgOnly ? (await detectAgentKinds(agentDetectionPath)).join(",") || undefined : undefined);

  if (skillOnly && !effectiveAgentFlag) return failure(requestId, "INVALID_INIT_FLAGS", "--skill-only requires --agent or an existing .pi/.claude/.agents directory in the project.", ["Create the target Agent directory first, or pass --agent <pi|claude|.agents> explicitly."]);
  if (pkgOnly && skillOnly) return failure(requestId, "INVALID_INIT_FLAGS", "--pkg-only and --skill-only are mutually exclusive.", ["Use exactly one init scope flag."]);
  if (pkgOnly && agentFlag) return failure(requestId, "INVALID_INIT_FLAGS", "--pkg-only and --agent are mutually exclusive; --pkg-only installs only the unity package.", ["Remove --agent or --pkg-only."]);
  let agents: ReturnType<typeof parseAgentList> = [];
  try {
    if (effectiveAgentFlag) agents = parseAgentList(effectiveAgentFlag);
  } catch (error) {
    if (error instanceof SkillInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
    throw error;
  }

  // check 不写项目文件；canonical identity 保证 alias 与 install 共用同一 named mutex。
  if (isCheck) {
    try {
      const canonicalProjectPath = checkIdentity!.canonicalProjectPath;
      const checkDeadlineMs = Date.now() + Math.max(1, Number(getString(flags, "timeout-ms")) || 60_000);
      const result: Record<string, unknown> = { action: "init_check", projectPath: canonicalProjectPath };
      if (!skillOnly) {
        try {
          result.package = { status: "installed", bridge: await checkPackageBridge(canonicalProjectPath), nextActions: ["Unity package com.alpsckr.unitycli is installed via copy mode in Packages/com.alpsckr.unitycli/."] };
        } catch (error) {
          if (!(error instanceof PackageManagerError)) throw error;
          result.package = { status: "not_installed", code: error.code, message: error.message };
        }
      }
      if (agents.length > 0) {
        try {
          result.skills = await agentSkillInstaller.checkForAgents(agents, await findSkillSourcePath(), canonicalProjectPath, checkDeadlineMs);
        } catch (error) {
          if (error instanceof SkillInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
          throw error;
        }
      }
      if (!skillOnly) {
        const { checkExtensionBundles } = await import("../extensions/installer.js");
        const physicalState = await checkExtensionBundles(canonicalProjectPath);
        result.extensionPhysicalState = physicalState;
        result.extensionSummary = {
          total: physicalState.length,
          dependencyInstalled: physicalState.filter((item) => item.dependencyInstalled).length,
          installed: physicalState.filter((item) => item.installed).length,
          unhealthy: physicalState.filter((item) => !item.installed || !item.dependencyInstalled).length
        };
        const { autoDetectExtensions } = await import("./extension-auto-detect.js");
        result.extensions = await autoDetectExtensions(canonicalProjectPath, true);
      }
      return success(requestId, result);
    } catch (error) {
      if (error instanceof ProjectIdentityError) return failure(requestId, error.code, error.message, ["Pass the root of an existing Unity project."], error.details);
      throw error;
    }
  }

  try {
    // 所有可预见校验和源/目标决议必须在唯一 effect barrier 前完成。
    const identity = resolveProjectIdentity(projectPath);
    const bridgePlan = skillOnly ? undefined : await preparePackageBridgeInstall(projectPath);
    const skillSource = agents.length > 0 ? await findSkillSourcePath() : undefined;
    const { autoDetectExtensions, autoDetectSummary } = await import("./extension-auto-detect.js");
    const extensionPlan = !skillOnly && !pkgOnly ? await autoDetectExtensions(projectPath, true) : undefined;
    const absoluteDeadlineMs = Date.now() + Math.max(1, Number(getString(flags, "timeout-ms")) || 60_000);
    {
      const result: Record<string, unknown> = { action: "init", projectPath: identity.canonicalProjectPath, projectIdentity: identity };
      if (bridgePlan) {
        const bridge = await ensurePackageBridge(projectPath, bridgePlan, { absoluteDeadlineMs });
        const bridgeReadback = await checkPackageBridge(projectPath);
        result.status = "package_installed";
        result.package = { status: "package_installed", bridge, readback: bridgeReadback };
        result.resolve = "filesystem_proven";
      }
      if (skillSource) {
        if (Date.now() >= absoluteDeadlineMs) throw new PackageManagerError("E_EXECUTION_TIMEOUT", "The immutable init deadline elapsed before skill installation.");
        const skills = await agentSkillInstaller.installForAgents(agents, skillSource, identity.canonicalProjectPath, absoluteDeadlineMs);
        result.skills = await agentSkillInstaller.checkForAgents(agents, skillSource, identity.canonicalProjectPath, absoluteDeadlineMs);
        result.skillChanges = skills.filter((item) => item.status === "updated").map((item) => ({ agent: item.agent, action: "overwritten", previousHash: item.previousHash, newHash: item.newHash }));
      }
      if (extensionPlan) {
        if (Date.now() >= absoluteDeadlineMs) throw new PackageManagerError("E_EXECUTION_TIMEOUT", "The immutable init deadline elapsed before extension installation.");
        const installed = await autoDetectExtensions(projectPath);
        const { checkExtensionBundles } = await import("../extensions/installer.js");
        result.extensions = installed;
        result.extensionSummary = autoDetectSummary(installed);
        result.extensionPhysicalState = await checkExtensionBundles(projectPath);
      }
      result.nextActions = ["Open or refresh the Unity project to import the copied package."];
      return success(requestId, result);
    }
  } catch (error) {
    if (error instanceof ProjectIdentityError) return failure(requestId, error.code, error.message, ["Pass the root of an existing Unity project."], error.details);
    if (error instanceof SkillInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
    if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details,
      error.transient === undefined ? undefined : { transient: error.transient, retryAfterMs: error.retryAfterMs });
    return failure(requestId, "E_EXECUTION_TIMEOUT", "The init result cannot be confirmed after the local operation stopped. Inspect the package, Skill, and extension state before retrying.", ["Check the project business state before submitting init again."], { reason: error instanceof Error ? error.message : String(error) });
  }
}
