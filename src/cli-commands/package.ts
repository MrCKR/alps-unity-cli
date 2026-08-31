import { resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { failure, success } from "../output.js";
import { ProjectIdentityError, resolveProjectIdentity } from "../project-identity.js";
import type { CliResult } from "../types.js";
import { handleInitDiscover } from "./init-discovery.js";
import { handleInitHelp } from "./init-help.js";
import { handleInitSkill } from "./init-skill.js";
import type { Runtime } from "./types.js";

function initTimeout(flags: Record<string, string | boolean>): number | undefined {
  const raw = getString(flags, "timeout-ms");
  if (raw === undefined) return 60_000;
  const value = Number(raw);
  return Number.isSafeInteger(value) && value > 0 ? value : undefined;
}

/** 只处理明确 Unity 项目的 canonical Bridge Package 安装或只读检查。 */
async function handleInitPackage(args: string[], flags: Record<string, string | boolean>, requestId: string): Promise<CliResult> {
  if (args.length) return failure(requestId, "E_INIT_ARGUMENT_UNEXPECTED", "init package does not accept positional arguments.", ["Run `unitycli init package --project <UnityProject>` without additional positional arguments."]);
  const projectFlag = getString(flags, "project");
  if (!projectFlag) return failure(requestId, "E_INIT_PROJECT_REQUIRED", "init package requires --project <UnityProject>.", ["Pass the root of one existing Unity project."], { option: "--project" });
  const timeoutMs = initTimeout(flags);
  if (timeoutMs === undefined) return failure(requestId, "E_INIT_TIMEOUT_INVALID", "init package --timeout-ms must be a positive integer.", ["Pass --timeout-ms <positive-integer>."], { option: "--timeout-ms" });
  try {
    const identity = resolveProjectIdentity(resolve(projectFlag));
    const { ensurePackageBridge, inspectPackageBridge, preparePackageBridgeInstall, PackageManagerError } = await import("../package-manager.js");
    if (getBoolean(flags, "check")) {
      const inspection = await inspectPackageBridge(identity.canonicalProjectPath);
      return success(requestId, {
        action: "init_package_check",
        projectPath: identity.canonicalProjectPath,
        package: inspection,
        nextActions: inspection.status === "installed" ? [] : ["Run `unitycli init package --project <UnityProject>` after resolving any reported assembly conflict."],
      });
    }
    const plan = await preparePackageBridgeInstall(identity.canonicalProjectPath);
    const install = await ensurePackageBridge(identity.canonicalProjectPath, plan, { absoluteDeadlineMs: Date.now() + timeoutMs });
    const readback = await inspectPackageBridge(identity.canonicalProjectPath);
    if (readback.status !== "installed") throw new PackageManagerError("INIT_CHECK_FAILED", "Bridge package installation did not reach an installed readback state.", ["Inspect the returned package state before submitting init again."], { package: readback });
    return success(requestId, {
      action: "init_package",
      projectPath: identity.canonicalProjectPath,
      projectIdentity: identity,
      package: { status: "installed", install, readback },
      nextActions: ["Open or refresh this Unity project to import the copied Bridge package."],
    });
  } catch (error) {
    if (error instanceof ProjectIdentityError) return failure(requestId, error.code, error.message, ["Pass the root of an existing Unity project."], error.details);
    const { PackageManagerError } = await import("../package-manager.js");
    if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions, error.details);
    return failure(requestId, "INIT_PACKAGE_FAILED", error instanceof Error ? error.message : String(error), ["Inspect the project and bundled package source before retrying."]);
  }
}

/** 唯一 init 控制面分发；不保留裸 init 或旧混装 flag。 */
export async function handleInit(command: string | undefined, args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (command === "help") return handleInitHelp(args, flags, requestId);
  if (command === "discover") return handleInitDiscover(args, flags, requestId);
  if (command === "package") return handleInitPackage(args, flags, requestId);
  if (command === "skill") return handleInitSkill(args, flags, runtime, requestId);
  return failure(requestId, "UNKNOWN_INIT_COMMAND", command ? `Unknown init command: ${command}.` : "init requires help, discover, package, or skill.", ["Run `unitycli init help`."]);
}
