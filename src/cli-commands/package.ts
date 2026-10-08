import { resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { failure, success } from "../output.js";
import { ProjectIdentityError, resolveProjectIdentity } from "../project-identity.js";
import { refreshInstalledExtensionProjections } from "../extensions/installer.js";
import type { CliResult } from "../types.js";
import { closeUnityProjectProcesses, findUnityProjectProcesses } from "../unity-project-processes.js";
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
async function handleInitPackage(args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (args.length) return failure(requestId, "E_INIT_ARGUMENT_UNEXPECTED", "init package does not accept positional arguments.", ["Run `unitycli init package --project <UnityProject>` without additional positional arguments."]);
  const projectFlag = getString(flags, "project");
  if (!projectFlag) return failure(requestId, "E_INIT_PROJECT_REQUIRED", "init package requires --project <UnityProject>.", ["Pass the root of one existing Unity project."], { option: "--project" });
  const timeoutMs = initTimeout(flags);
  if (timeoutMs === undefined) return failure(requestId, "E_INIT_TIMEOUT_INVALID", "init package --timeout-ms must be a positive integer.", ["Pass --timeout-ms <positive-integer>."], { option: "--timeout-ms" });
  const check = getBoolean(flags, "check");
  const closeEditor = getBoolean(flags, "close-editor");
  if (check && closeEditor) return failure(requestId, "E_INIT_CLOSE_EDITOR_CHECK_FORBIDDEN", "init package --check is read-only and cannot close Unity Editor.", ["Remove either --check or --close-editor."], { option: "--close-editor" });
  const absoluteDeadlineMs = Date.now() + timeoutMs;
  const remaining = (): number => Math.max(0, absoluteDeadlineMs - Date.now());
  try {
    const identity = resolveProjectIdentity(resolve(projectFlag));
    const { ensurePackageBridge, inspectPackageBridge, preparePackageBridgeInstall, packageAssemblyConflictNextActions, PackageManagerError } = await import("../package-manager.js");
    if (check) {
      const inspection = await inspectPackageBridge(identity.canonicalProjectPath);
      // 冲突指引与安装路径复用同一 owner，避免只报告状态却不给出处理办法。
      const nextActions = inspection.status === "installed" ? []
        : inspection.status === "conflict"
          ? packageAssemblyConflictNextActions()
          : ["Run `unitycli init package --project <UnityProject>` to converge the Bridge package to the bundled version."];
      return success(requestId, { action: "init_package_check", projectPath: identity.canonicalProjectPath, package: inspection, nextActions });
    }

    const findEditors = runtime.findUnityProjectProcesses ?? findUnityProjectProcesses;
    let editors;
    try {
      const observationBudgetMs = Math.min(3_000, remaining());
      if (observationBudgetMs < 1) throw new Error("installation deadline elapsed before Editor observation");
      editors = await findEditors(identity.instanceId, identity.canonicalProjectPath, observationBudgetMs);
    } catch {
      throw new PackageManagerError("E_EDITOR_STATE_UNVERIFIABLE", "The exact target Unity Editor process state could not be verified before package installation.", ["Verify the target project is not open in Unity, then rerun init package."], { phase: "editor-observation" });
    }

    let editorClose: { status: "not-running" | "closed"; requested: number } | undefined;
    if (editors.length > 0 && !closeEditor) {
      throw new PackageManagerError("E_INIT_EDITOR_RUNNING", "The exact target Unity project is open and may hold Bridge package files.", ["Ask the user whether to close this exact Unity project, then rerun with --close-editor after approval."], { editorCount: editors.length });
    }
    if (closeEditor) {
      if (editors.length === 0) editorClose = { status: "not-running", requested: 0 };
      else {
        const closeEditors = runtime.closeUnityProjectProcesses ?? closeUnityProjectProcesses;
        let closeResult;
        try {
          const closeBudgetMs = remaining();
          if (closeBudgetMs < 1) throw new Error("installation deadline elapsed before Editor close");
          closeResult = await closeEditors(editors, closeBudgetMs);
        } catch {
          throw new PackageManagerError("E_EDITOR_CLOSE_FAILED", "The exact target Unity Editor could not be closed normally.", ["Close the exact target project normally, then rerun init package without --close-editor."], { phase: "editor-close" });
        }
        if (closeResult.remaining > 0) {
          throw new PackageManagerError("E_EDITOR_CLOSE_BLOCKED", "The exact target Unity Editor remained open after a normal close request.", ["Resolve any unsaved-scene or modal prompt in Unity, close the project normally, then rerun init package."], { requested: closeResult.requested, remaining: closeResult.remaining });
        }
        editorClose = { status: "closed", requested: closeResult.requested };
      }
    }

    if (remaining() < 1) throw new PackageManagerError("E_EXECUTION_TIMEOUT", "The immutable init deadline elapsed before Bridge package publication.", ["Inspect the project package state before submitting init again."]);
    const plan = await preparePackageBridgeInstall(identity.canonicalProjectPath);
    if (remaining() < 1) throw new PackageManagerError("E_EXECUTION_TIMEOUT", "The immutable init deadline elapsed during Bridge package prevalidation.", ["Inspect the project package state before submitting init again."]);
    const install = await ensurePackageBridge(identity.canonicalProjectPath, plan, { absoluteDeadlineMs });
    const readback = await inspectPackageBridge(identity.canonicalProjectPath);
    if (readback.status !== "installed") throw new PackageManagerError("INIT_CHECK_FAILED", "Bridge package installation did not reach an installed readback state.", ["Inspect the returned package state before submitting init again."], { package: readback });
    // 升级自愈：包发布后把已装扩展投影对齐到本版本内置 bundle，避免 Unity 增量编译漏编扩展程序集导致 bridge 启动校验失败；失败逐项透出且不回滚已发布的包。
    const extensionRepair = await refreshInstalledExtensionProjections(identity.canonicalProjectPath, absoluteDeadlineMs);
    const warnings = extensionRepair.failures.map((repairFailure) => `${repairFailure.extensionId}: ${repairFailure.code} — ${repairFailure.message} ${repairFailure.nextActions[0] ?? ""}`.trim());
    return success(requestId, {
      action: "init_package",
      projectPath: identity.canonicalProjectPath,
      projectIdentity: identity,
      ...(editorClose ? { editorClose } : {}),
      package: { status: "installed", install, readback },
      ...(extensionRepair.repaired.length > 0 || extensionRepair.failures.length > 0 ? { extensionRepair } : {}),
      nextActions: [
        "Open or refresh this Unity project to import the copied Bridge package.",
        ...(extensionRepair.repaired.length > 0 ? ["Switch focus to Unity Editor so it recompiles the refreshed extension projections before the next bridge call."] : []),
      ],
    }, warnings.length > 0 ? { warnings } : undefined);
  } catch (error) {
    if (error instanceof ProjectIdentityError) return failure(requestId, error.code, error.message, ["Pass the root of an existing Unity project."], error.details);
    const { PackageManagerError } = await import("../package-manager.js");
    if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions, error.details, { transient: error.transient, retryAfterMs: error.retryAfterMs });
    return failure(requestId, "INIT_PACKAGE_FAILED", error instanceof Error ? error.message : String(error), ["Inspect the project and bundled package source before retrying."]);
  }
}

/** 唯一 init 控制面分发；不保留裸 init 或旧混装 flag。 */
export async function handleInit(command: string | undefined, args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (command === "help") return handleInitHelp(args, flags, requestId);
  if (command === "discover") return handleInitDiscover(args, flags, runtime, requestId);
  if (command === "package") return handleInitPackage(args, flags, runtime, requestId);
  if (command === "skill") return handleInitSkill(args, flags, runtime, requestId);
  return failure(requestId, "UNKNOWN_INIT_COMMAND", command ? `Unknown init command: ${command}.` : "init requires help, discover, package, or skill.", ["Run `unitycli init help`."]);
}
