import { existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { executeBridgePayload } from "../bridge/index.js";
import { confirmDestructiveExecution } from "../security.js";
import { failure, success } from "../output.js";
import { handlePackageInfoCommand } from "../package-info.js";
import { defaultDataDir, discoverUnity } from "../platform.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { isProjectLocked, runUnityJob } from "./unity-job.js";
import type { BridgeRuntime } from "../bridge/index.js";
import type { JobRunner } from "../job-runner.js";
import { StructuredToolError } from "../tools/errors.js";
import { InstanceRegistryError, selectInstanceForFlags } from "../instances.js";
import { executeWithLocalRequestJournal } from "../local-request-journal.js";
import { ProjectIdentityError, resolveProjectIdentity } from "../project-identity.js";

export async function handleInit(flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const { checkPackageBridge, ensurePackageBridge, preparePackageBridgeInstall, PackageManagerError } = await import("../package-manager.js");
  const { agentSkillInstaller, detectAgentKinds, findSkillSourcePath, parseAgentList, SkillInstallerError } = await import("./skill-installer.js");
  const agentFlag = getString(flags, "agent");
  const pkgOnly = getBoolean(flags, "pkg-only");
  const skillOnly = getBoolean(flags, "skill-only");
  const isCheck = getBoolean(flags, "check");
  const effectiveAgentFlag = agentFlag ?? (!pkgOnly ? (await detectAgentKinds(projectPath)).join(",") || undefined : undefined);

  if (skillOnly && !effectiveAgentFlag) return failure(requestId, "INVALID_INIT_FLAGS", "--skill-only requires --agent or an existing .pi/.claude/.agents directory in the project.", ["Create the target Agent directory first, or pass --agent <pi|claude|.agents> explicitly."]);
  if (pkgOnly && skillOnly) return failure(requestId, "INVALID_INIT_FLAGS", "--pkg-only and --skill-only are mutually exclusive.", ["Use exactly one init scope flag."]);
  if (pkgOnly && agentFlag) return failure(requestId, "INVALID_INIT_FLAGS", "--pkg-only and --agent are mutually exclusive; --pkg-only installs only the unity package.", ["Remove --agent or --pkg-only."]);
  let agents: ReturnType<typeof parseAgentList> = [];
  try { if (effectiveAgentFlag) agents = parseAgentList(effectiveAgentFlag); } catch (error) {
    if (error instanceof SkillInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
    throw error;
  }

  // check 是严格纯读路径：不创建 journal、lock、目录或任何安装投影。
  if (isCheck) {
    const result: Record<string, unknown> = { action: "init_check", projectPath };
    if (!skillOnly) {
      try { result.package = { status: "installed", bridge: await checkPackageBridge(projectPath), nextActions: ["Unity package com.unity_cli is installed via copy mode in Packages/com.unity_cli/."] }; }
      catch (error) {
        if (!(error instanceof PackageManagerError)) throw error;
        result.package = { status: "not_installed", code: error.code, message: error.message };
      }
    }
    if (agents.length > 0) result.skills = await agentSkillInstaller.checkForAgents(agents, await findSkillSourcePath(), projectPath);
    if (!skillOnly) {
      const { checkExtensionBundles } = await import("../extensions/installer.js");
      const physicalState = await checkExtensionBundles(projectPath);
      result.extensionPhysicalState = physicalState;
      result.extensionSummary = { total: physicalState.length, dependencyInstalled: physicalState.filter((item) => item.dependencyInstalled).length, installed: physicalState.filter((item) => item.installed).length, unhealthy: physicalState.filter((item) => !item.installed || !item.dependencyInstalled).length };
      const { autoDetectExtensions } = await import("./extension-auto-detect.js");
      result.extensions = await autoDetectExtensions(projectPath, true);
    }
    return success(requestId, result);
  }

  try {
    // 所有可预见校验和源/目标决议必须在唯一 effect barrier 前完成。
    const identity = resolveProjectIdentity(projectPath);
    const bridgePlan = skillOnly ? undefined : await preparePackageBridgeInstall(projectPath);
    const skillSource = agents.length > 0 ? await findSkillSourcePath() : undefined;
    const skillPlan = skillSource ? await agentSkillInstaller.checkForAgents(agents, skillSource, projectPath) : [];
    const { autoDetectExtensions, autoDetectSummary } = await import("./extension-auto-detect.js");
    const extensionPlan = !skillOnly && !pkgOnly ? await autoDetectExtensions(projectPath, true) : undefined;
    const journalInput = { projectIdentity: identity, flags: { pkgOnly, skillOnly, agents }, bridgePlan, skillPlan, extensionPlan };
    const absoluteDeadlineMs = executionDeadlineMs(flags);

    return await executeWithLocalRequestJournal({
      identity,
      requestId,
      command: "init",
      toolName: "init",
      input: journalInput,
      projectPath,
      dataDir: runtime.dataDir ?? defaultDataDir(),
      absoluteDeadlineMs,
      executionClass: "atomic",
      completionDriverId: "init-filesystem-readback",
    }, async (commitEffect) => {
      // 资源锁取得后重新冻结全部输入；任何漂移都必须发生在首个 effect 前。
      const freshBridgePlan = skillOnly ? undefined : await preparePackageBridgeInstall(projectPath);
      const freshSkillSource = agents.length > 0 ? await findSkillSourcePath() : undefined;
      const freshSkillPlan = freshSkillSource ? await agentSkillInstaller.checkForAgents(agents, freshSkillSource, projectPath) : [];
      const freshExtensionPlan = !skillOnly && !pkgOnly ? await autoDetectExtensions(projectPath, true) : undefined;
      if (JSON.stringify(freshBridgePlan) !== JSON.stringify(bridgePlan)
        || freshSkillSource !== skillSource
        || JSON.stringify(freshSkillPlan) !== JSON.stringify(skillPlan)
        || JSON.stringify(freshExtensionPlan) !== JSON.stringify(extensionPlan)) {
        return failure(requestId, "INIT_PLAN_STALE", "Init source, target, or extension state changed while waiting for the instance resource lock.", [
          "Submit init again so it can build a fresh transaction plan."
        ]);
      }
      if (Date.now() >= absoluteDeadlineMs) {
        return failure(requestId, "E_EXECUTION_TIMEOUT", "The immutable init deadline elapsed before the first filesystem effect.", [
          "Submit init again with a timeout that allows the current local operation to finish."
        ]);
      }
      await commitEffect();
      const result: Record<string, unknown> = { action: "init", projectPath: identity.canonicalProjectPath, projectIdentity: identity };
      if (bridgePlan) {
        const bridge = await ensurePackageBridge(projectPath, bridgePlan);
        const bridgeReadback = await checkPackageBridge(projectPath);
        const liveRefresh = await refreshOpenEditor(requestId, flags, runtime, absoluteDeadlineMs);
        result.status = "package_installed";
        result.package = { status: "package_installed", bridge, readback: bridgeReadback, liveRefresh };
        result.liveRefresh = liveRefresh;
        result.resolve = liveRefresh.status === "completed" ? "live_bridge_refresh" : "skipped";
      }
      if (skillSource) {
        if (Date.now() >= absoluteDeadlineMs) throw new PackageManagerError("E_EXECUTION_TIMEOUT", "The immutable init deadline elapsed before skill installation.");
        const skills = await agentSkillInstaller.installForAgents(agents, skillSource, projectPath);
        // 每个 skill 都必须从目标重新哈希，不能复用安装前状态或 copy 返回值。
        result.skills = await agentSkillInstaller.checkForAgents(agents, skillSource, projectPath);
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
      result.nextActions = result.liveRefresh && (result.liveRefresh as { status: string }).status === "completed"
        ? ["Wait for Unity to finish compiling/importing before using Bridge commands."]
        : ["Open the Unity project to import the copied package."];
      return success(requestId, result);
    });
  } catch (error) {
    if (error instanceof ProjectIdentityError) return failure(requestId, error.code, error.message, ["Pass the root of an existing Unity project."], error.details);
    if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details,
      error.transient === undefined ? undefined : { transient: error.transient, retryAfterMs: error.retryAfterMs });
    return failure(requestId, "E_REQUEST_JOURNAL_IO", error instanceof Error ? error.message : String(error), ["Verify the Unity CLI data directory is writable before retrying."]);
  }
}

export async function handlePackage(command: string | undefined, args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult | undefined> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const {
    PackageManagerError,
    installPackage,
    packageInfo,
    parsePackageSpec,
    readSnapshot,
    removePackage,
    removePackageBridge,
    restoreDeployedPackage,
    deployPackageSource,
    searchRegistry,
    validatePackageName,
    validateRegistryUrl
  } = await import("../package-manager.js");

  if (!command) {
    return handlePackageInfoCommand(flags, requestId);
  }

  try {
    if (command === "list") {
    return success(requestId, await readSnapshot(projectPath));
  }

  if (command === "status") {
    const snapshot = await readSnapshot(projectPath);
    return success(requestId, {
      projectPath,
      manifestPath: snapshot.manifestPath,
      lockPath: snapshot.lockPath,
      packageCount: snapshot.packages.length,
      registry: snapshot.registry ?? null,
      scopedRegistryCount: snapshot.scopedRegistries.length,
      packages: snapshot.packages.map((entry) => ({
        name: entry.name,
        requestedVersion: entry.requestedVersion ?? null,
        lockedVersion: entry.lockedVersion ?? null,
        source: entry.source ?? null,
        resolved: entry.lockedVersion !== undefined
      }))
    });
  }

  if (command === "info") {
    const name = validatePackageName(args[0] ?? getString(flags, "name"));
    const info = await packageInfo(projectPath, name);
    return info ? success(requestId, { projectPath, package: info }) : failure(requestId, "PACKAGE_NOT_FOUND", `Package not found: ${name}`, ["Run tools run package.list to inspect installed packages."]);
  }

  if (command === "search") {
    const query = validatePackageName(args[0] ?? getString(flags, "query"));
    const registry = validateRegistryUrl(getString(flags, "registry") ?? (await readSnapshot(projectPath)).registry ?? "https://package.openupm.com");
    return success(requestId, { registry, query, packages: await searchRegistry(registry, query) });
  }

  if (command === "install") {
    const spec = parsePackageSpec(validatePackageName(args[0] ?? getString(flags, "name")), getString(flags, "version"));
    const confirmation = await confirmDestructiveExecution(flags, {
      command: "package.install",
      summary: "Package Install",
      destructiveHint: true,
      requestId
    });
    if (confirmation) {
      return confirmation;
    }
    return executeLocalPackageMutation("package.install", { spec, flags }, projectPath, flags, runtime, requestId, async (commitEffect) => {
      await commitEffect();
      const manifest = await installPackage(projectPath, spec);
      return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "install", package: spec.name, ...manifest });
    });
  }

  if (command === "remove") {
    const name = validatePackageName(args[0] ?? getString(flags, "name"));
    const confirmation = await confirmDestructiveExecution(flags, {
      command: "package.remove",
      summary: "Package Remove",
      destructiveHint: true,
      supportsDryRun: true,
      requestId
    });
    if (confirmation) {
      return confirmation;
    }
    const removeOptions = { force: getBoolean(flags, "force"), dryRun: getBoolean(flags, "dry-run") };
    if (removeOptions.dryRun) {
      const manifest = await removePackage(projectPath, name, removeOptions);
      return success(requestId, { action: "remove", package: name, projectPath, ...manifest, status: manifest.blocked ? "blocked" : "dry_run" });
    }
    return executeLocalPackageMutation("package.remove", { name, removeOptions }, projectPath, flags, runtime, requestId, async (commitEffect) => {
      await commitEffect();
      const manifest = await removePackage(projectPath, name, removeOptions);
      return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "remove", package: name, ...manifest });
    });
  }

  if (command === "resolve") {
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "resolve" });
  }

  if (command === "bridge-remove") {
    const confirmation = await confirmDestructiveExecution(flags, {
      command: "package.bridge-remove",
      summary: "Package Bridge Remove",
      destructiveHint: true,
      supportsDryRun: true,
      requestId
    });
    if (confirmation) {
      return confirmation;
    }
    const options = { dryRun: getBoolean(flags, "dry-run") };
    if (options.dryRun) return success(requestId, { action: "bridge-remove", ...(await removePackageBridge(projectPath, options)) });
    return executeLocalPackageMutation("package.bridge-remove", {}, projectPath, flags, runtime, requestId, async (commitEffect) => {
      await commitEffect();
      return success(requestId, { action: "bridge-remove", ...(await removePackageBridge(projectPath, options)) });
    });
  }

  if (command === "deploy") {
    const confirmation = await confirmDestructiveExecution(flags, {
      command: "package.deploy",
      summary: "Package Deploy",
      destructiveHint: true,
      supportsDryRun: true,
      requestId
    });
    if (confirmation) {
      return confirmation;
    }
    const source = args[0] ?? getString(flags, "source") ?? getString(flags, "source-path") ?? getString(flags, "path");
    if (!source) {
      return failure(requestId, "PACKAGE_DEPLOY_SOURCE_REQUIRED", "package.deploy requires a source package folder.", [
        "Pass --input {\"source\":\"<local-package-folder>\"} or --source <local-package-folder>.",
        "The source folder must contain package.json with name/version and must be outside the target project's Packages directory."
      ]);
    }
    const deployOptions = { packageName: getString(flags, "package-name") ?? getString(flags, "packageName") ?? getString(flags, "name"), dryRun: getBoolean(flags, "dry-run") };
    if (deployOptions.dryRun) return success(requestId, { action: "deploy", status: "dry_run", ...(await deployPackageSource(projectPath, source, deployOptions)) });
    return executeLocalPackageMutation("package.deploy", { source, deployOptions }, projectPath, flags, runtime, requestId, async (commitEffect, absoluteDeadlineMs) => {
      await commitEffect();
      const deployed = await deployPackageSource(projectPath, source, deployOptions);
      const liveRefresh = await refreshOpenEditor(requestId, flags, runtime, absoluteDeadlineMs);
      return success(requestId, { action: "deploy", status: "deployed", ...deployed, liveRefresh, nextActions: liveRefresh.status === "completed"
        ? ["Wait for Unity to finish importing and compiling, then inspect editor.state or console logs."]
        : ["Open or refresh the Unity project so it imports the deployed package, then inspect console diagnostics."] });
    });
  }

  if (command === "restore") {
    const confirmation = await confirmDestructiveExecution(flags, {
      command: "package.restore",
      summary: "Package Restore",
      destructiveHint: true,
      supportsDryRun: true,
      requestId
    });
    if (confirmation) {
      return confirmation;
    }
    const packageName = args[0] ?? getString(flags, "package-name") ?? getString(flags, "packageName") ?? getString(flags, "name");
    if (!packageName) {
      return failure(requestId, "PACKAGE_NAME_REQUIRED", "package.restore requires packageName.", [
        "Pass --input {\"packageName\":\"<package>\"} or --package-name <package>.",
        "Use package.deploy output or Packages/.unity_cli_deploy_backups/unity_cli.package-deployments.json to find deployed package names."
      ]);
    }
    const restoreOptions = { dryRun: getBoolean(flags, "dry-run") };
    if (restoreOptions.dryRun) return success(requestId, { action: "restore", status: "dry_run", ...(await restoreDeployedPackage(projectPath, packageName, restoreOptions)) });
    return executeLocalPackageMutation("package.restore", { packageName }, projectPath, flags, runtime, requestId, async (commitEffect, absoluteDeadlineMs) => {
      await commitEffect();
      const restored = await restoreDeployedPackage(projectPath, packageName, restoreOptions);
      const liveRefresh = await refreshOpenEditor(requestId, flags, runtime, absoluteDeadlineMs);
      return success(requestId, { action: "restore", status: "restored", ...restored, liveRefresh, nextActions: liveRefresh.status === "completed"
        ? ["Wait for Unity to finish importing and compiling, then inspect editor.state or console logs."]
        : ["Open or refresh the Unity project so it imports the restored package, then inspect console diagnostics."] });
    });
  }

  // 未知子命令放行到 bridge spec 直通（如 package.embed），与 handleBuild 一致。
  // 主路由会 lookup bridgeCommandRegistry("package", command)，找不到再返回 UNKNOWN_COMMAND。
  return undefined;
  } catch (error) {
    if (error instanceof PackageManagerError) {
      return failure(requestId, error.code, error.message, error.nextActions);
    }
    throw error;
  }
}

/** package 本地持久化命令统一走 durable request 与 canonical instance 资源锁。 */
async function executeLocalPackageMutation(
  toolName: string,
  input: Record<string, unknown>,
  projectPath: string,
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string,
  operation: (commitEffect: () => Promise<void>, absoluteDeadlineMs: number) => Promise<CliResult>,
): Promise<CliResult> {
  const identity = resolveProjectIdentity(projectPath);
  const absoluteDeadlineMs = executionDeadlineMs(flags);
  return executeWithLocalRequestJournal({
    identity,
    requestId,
    command: toolName,
    toolName,
    input,
    projectPath,
    dataDir: runtime.dataDir ?? defaultDataDir(),
    absoluteDeadlineMs,
    executionClass: "atomic",
    completionDriverId: "package-filesystem-readback",
  }, (commitEffect) => operation(commitEffect, absoluteDeadlineMs));
}

export interface PackageLiveRuntime extends BridgeRuntime {
  dataDir: string;
}

export interface PackageResolveRuntime extends PackageLiveRuntime {
  runner?: JobRunner;
}

export interface PackageExecutionOptions {
  projectPath: string;
  unity?: string;
  job?: boolean;
  wait?: boolean;
  timeoutMs?: number;
  waitTimeoutMs?: number;
  absoluteDeadlineMs?: number;
}

/**
 * 独立 package.resolve 在 effect barrier 前证明至少存在一条可交付路径。
 * install/remove 只以 manifest 持久化为业务终态，不调用此约束。
 */
export async function preflightPackageResolveDelivery(options: PackageExecutionOptions, runtime: PackageResolveRuntime): Promise<void> {
  const manifestPath = join(options.projectPath, "Packages", "manifest.json");
  if (!existsSync(manifestPath)) {
    throw new StructuredToolError("MANIFEST_NOT_FOUND", `Unity manifest not found: ${manifestPath}`, ["Run the command from a Unity project root or pass --project <path>."], { projectPath: options.projectPath, manifestPath });
  }
  if (runtime.bridgeExecute) return;

  try {
    const selected = await selectInstanceForFlags({ instance: options.projectPath }, runtime.dataDir);
    if (selected.instance.activeSession && (selected.instance.status === "ready" || selected.instance.status === "transitioning")) return;
    if (selected.instance.status === "blocked" || selected.instance.status === "conflict") {
      throw new StructuredToolError("UNITY_BUSY", "The selected Unity project has no uniquely usable Editor session for package resolve.", ["Inspect editor.state and resolve the blocker or instance conflict before retrying."], { projectPath: options.projectPath, status: selected.instance.status });
    }
  } catch (error) {
    if (error instanceof StructuredToolError) throw error;
    if (error instanceof InstanceRegistryError) {
      if (["E_INSTANCE_NOT_FOUND", "E_INSTANCE_NOT_RUNNING", "E_INSTANCE_REQUIRED"].includes(error.code)) {
        // 没有 live claim 时继续检查 batchmode delivery。
      } else {
        throw new StructuredToolError(error.code, error.message, error.nextActions, error.details);
      }
    } else {
      throw error;
    }
  }

  if (!options.job) {
    throw new StructuredToolError("E_PACKAGE_RESOLVE_DELIVERY_REQUIRED", "Package resolve cannot report success until a live Unity session performs a terminal resolve.", ["Open this exact project in Unity Editor and retry.", "Pass --job with --unity <path-to-Unity.exe> to use the frozen batch job delivery."], { projectPath: options.projectPath, resolve: "unavailable" });
  }

  const unityPath = options.unity ?? discoverUnity();
  if (!unityPath) {
    throw new StructuredToolError("UNITY_NOT_FOUND", "Package resolve cannot report success until Unity performs a terminal resolve.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE.", "Open this exact project in Unity Editor and retry."], { projectPath: options.projectPath, resolve: "unavailable" });
  }
  if (isProjectLocked(options.projectPath)) {
    throw new StructuredToolError("UNITY_BUSY", "Cannot start Unity batchmode while this project is locked without a validated live Bridge session.", ["Close the locking Editor or restore its validated Bridge listener before retrying."], { projectPath: options.projectPath, resolve: "blocked_by_open_editor" });
  }
  if (!runtime.runner) throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Package resolve requires a job runner runtime for batchmode execution.");
}

/** 通过 session supervisor 通知已打开的 Editor，并返回 Bridge 已证明的终态。 */
export async function refreshPackageEditor(requestId: string, runtime: PackageLiveRuntime, projectPath?: string, timeoutMs = 15_000, absoluteDeadlineMs?: number): Promise<Record<string, unknown>> {
  const deadline = absoluteDeadlineMs;
  const deadlineRemainingMs = deadline === undefined ? undefined : deadline - Date.now();
  if (deadlineRemainingMs !== undefined && deadlineRemainingMs <= 0) {
    throw new StructuredToolError("E_EXECUTION_TIMEOUT", "The immutable package operation deadline elapsed before the Editor refresh started.", [
      "Inspect the package files and Unity Package Manager state before submitting another request."
    ], { projectPath, absoluteDeadlineUtc: new Date(deadline!).toISOString() });
  }
  const remainingMs = Math.min(
    Number.isFinite(timeoutMs) ? Math.max(1, Math.trunc(timeoutMs)) : 15_000,
    deadlineRemainingMs === undefined ? Number.MAX_SAFE_INTEGER : deadlineRemainingMs,
  );
  const result = await executeBridgePayload(`${requestId}_refresh`, "bridge.package.refresh", {}, {
    ...(projectPath ? { instance: projectPath } : {}),
    "timeout-ms": String(remainingMs)
  }, runtime, undefined, { mode: "execute", mutation: true, defaultTimeoutMs: remainingMs });
  if (result.ok) return { status: "completed", command: "bridge.package.refresh", result: result.data ?? null };
  if (["E_INSTANCE_REQUIRED", "E_INSTANCE_NOT_FOUND", "E_INSTANCE_NOT_RUNNING"].includes(result.error.code)) {
    return { status: "skipped", reason: "editor_not_running" };
  }
  return { status: "failed", command: "bridge.package.refresh", code: result.error.code, message: result.error.message };
}

export async function refreshOpenEditor(requestId: string, flags: Record<string, string | boolean>, runtime: Runtime, absoluteDeadlineMs?: number): Promise<Record<string, unknown>> {
  const timeout = Number(getString(flags, "timeout-ms"));
  return refreshPackageEditor(requestId, runtime, getString(flags, "project"), Number.isFinite(timeout) && timeout > 0 ? timeout : undefined, absoluteDeadlineMs);
}

function executionDeadlineMs(flags: Record<string, string | boolean>): number {
  return Date.now() + Math.max(1, Number(getString(flags, "timeout-ms")) || 60_000);
}

/** 在 live refresh、跳过和 batch job 之间选择 package resolve 路径。 */
export async function resolvePackageChanges(action: string, options: PackageExecutionOptions, runtime: PackageResolveRuntime, requestId: string, result: Record<string, unknown>): Promise<Record<string, unknown>> {
  const { projectPath } = options;
  const absoluteDeadlineMs = options.absoluteDeadlineMs ?? Date.now() + Math.max(1, options.timeoutMs ?? 60_000);
  const remainingMs = () => {
    const remaining = absoluteDeadlineMs - Date.now();
    if (remaining <= 0) {
      throw new StructuredToolError("E_EXECUTION_TIMEOUT", "The immutable package operation deadline elapsed before Unity reached a terminal result.", [
        "Inspect the package manifest and Unity Package Manager state before submitting another request."
      ], { projectPath, absoluteDeadlineUtc: new Date(absoluteDeadlineMs).toISOString() });
    }
    return remaining;
  };
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  if (!existsSync(manifestPath)) {
    throw new StructuredToolError("MANIFEST_NOT_FOUND", `Unity manifest not found: ${manifestPath}`, [
      "Run the command from a Unity project root or pass --project <path>.",
      "package.resolve requires an existing Packages/manifest.json and does not create Unity projects."
    ], { projectPath, manifestPath, resolve: "blocked_missing_manifest" });
  }

  const liveRefresh = result.liveRefresh as Record<string, unknown> | undefined
    ?? await refreshPackageEditor(requestId, runtime, projectPath, Math.min(remainingMs(), Math.max(1, options.timeoutMs ?? 60_000)));
  if (liveRefresh.status === "completed") {
    return {
      ...result,
      projectPath,
      status: result.status ?? "manifest_updated",
      resolve: "live_bridge_refresh",
      liveRefresh,
      nextActions: result.nextActions ?? ["The open Unity Editor has been notified to refresh assets. Wait for Unity to finish reimporting before using the package."]
    };
  }

  if (liveRefresh.status === "failed") {
    throw new StructuredToolError("UNITY_BUSY", "The Unity Editor is open but the asset refresh command failed.", [
      "Wait for Unity to finish compiling/importing and retry.",
      "If the issue persists, inspect editor.state or close the Unity Editor before using the batch path.",
      "The manifest has been updated; Unity will detect the change on next domain reload or asset refresh."
    ], { projectPath, resolve: "blocked_by_open_editor", manifestUpdated: true, liveRefresh });
  }

  if (!options.job) {
    if (action === "resolve") {
      throw new StructuredToolError("E_PACKAGE_RESOLVE_DELIVERY_REQUIRED", "Package resolve cannot report success until a live Unity session performs a terminal resolve.", [
        "Open this exact project in Unity Editor and retry.",
        "Pass --job with --unity <path-to-Unity.exe> to use the frozen batch job delivery."
      ], { projectPath, resolve: "unavailable" });
    }
    return {
      ...result,
      projectPath,
      status: result.status ?? "manifest_updated",
      resolve: "skipped",
      liveRefresh,
      nextActions: result.nextActions ?? ["Open or refresh this Unity project to apply the persisted manifest change."]
    };
  }

  const unityPath = options.unity ?? discoverUnity();
  if (!unityPath) {
    throw new StructuredToolError("UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }
  if (isProjectLocked(projectPath)) {
    throw new StructuredToolError("UNITY_BUSY", "Cannot start Unity batchmode: the project is currently open in the Unity Editor.", [
      "Close the Unity Editor before using the batch path; live refresh is selected through --instance or CWD.",
      "The manifest has been updated; Unity will detect the change on next domain reload or asset refresh.",
      "Wait for the process holding <project>/Temp/UnityLockfile to exit, then retry"
    ], { projectPath, resolve: "blocked_by_open_editor", manifestUpdated: true });
  }

  if (!runtime.runner) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Package resolve requires a job runner runtime for batchmode execution.");
  }
  const { ensurePackageBridge } = await import("../package-manager.js");
  const bridge = await ensurePackageBridge(projectPath);
  return runUnityJob({
    kind: "package",
    requestId,
    project: projectPath,
    unity: unityPath,
    args: ["-executeMethod", "UnityCli.UnityCliBatch.ResolvePackages", "-unityCliPackageAction", action],
    result: { ...result, bridge },
    timeoutMs: Math.min(remainingMs(), Math.max(1, options.timeoutMs ?? 60_000)),
    wait: options.wait,
    waitTimeoutMs: options.waitTimeoutMs
  }, runtime.runner);
}

export async function maybeResolvePackage(command: string, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, projectPath: string, result: Record<string, unknown>): Promise<CliResult> {
  try {
    return success(requestId, await resolvePackageChanges(command, {
      projectPath,
      unity: getString(flags, "unity"),
      job: getBoolean(flags, "job"),
      wait: getBoolean(flags, "wait"),
      timeoutMs: Number(getString(flags, "timeout-ms")) || undefined,
      waitTimeoutMs: Number(getString(flags, "wait-timeout-ms")) || undefined
    }, runtime, requestId, result));
  } catch (error) {
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details);
    throw error;
  }
}
