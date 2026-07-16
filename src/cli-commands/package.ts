import { existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { executeBridgePayload } from "../bridge/index.js";
import { confirmDestructiveExecution } from "../security.js";
import { failure, success } from "../output.js";
import { handlePackageInfoCommand } from "../package-info.js";
import { discoverUnity } from "../platform.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { isProjectLocked, runUnityJob } from "./unity-job.js";
import type { BridgeRuntime } from "../bridge/index.js";
import type { JobRunner } from "../job-runner.js";
import { StructuredToolError } from "../tools/errors.js";

export async function handleInit(flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const { checkPackageBridge, ensurePackageBridge } = await import("../package-manager.js");
  const agentFlag = getString(flags, "agent");
  const pkgOnly = getBoolean(flags, "pkg-only");
  const skillOnly = getBoolean(flags, "skill-only");
  const isCheck = getBoolean(flags, "check");
  let effectiveAgentFlag = agentFlag;

  if (!agentFlag && !pkgOnly) {
    const { detectAgentKinds } = await import("./skill-installer.js");
    const detectedAgents = await detectAgentKinds(projectPath);
    effectiveAgentFlag = detectedAgents.length > 0 ? detectedAgents.join(",") : undefined;
  }

  // ─── Parameter validation ─────────────────────────────────────────────

  // --skill-only requires either --agent or an existing Agent config directory
  if (skillOnly && !effectiveAgentFlag) {
    return failure(requestId, "INVALID_INIT_FLAGS", "--skill-only requires --agent or an existing .pi/.claude/.agents directory in the project.", [
      "Create the target Agent directory first, or pass --agent <pi|claude|.agents> explicitly.",
      "Use --pkg-only to install only the Unity package."
    ]);
  }

  // --pkg-only and --skill-only are mutually exclusive
  if (pkgOnly && skillOnly) {
    return failure(requestId, "INVALID_INIT_FLAGS", "--pkg-only and --skill-only are mutually exclusive.", [
      "Use --pkg-only to install only the unity package.",
      "Use --skill-only --agent <agents> to install only agent skills.",
      "Omit both to install both unitypkg and agent skills."
    ]);
  }

  // --pkg-only and --agent are mutually exclusive
  if (pkgOnly && agentFlag) {
    return failure(requestId, "INVALID_INIT_FLAGS", "--pkg-only and --agent are mutually exclusive; --pkg-only installs only the unity package.", [
      "Use --pkg-only to install only the unity package without agent skills.",
      "Use --agent <agents> to install the unity package and agent skills."
    ]);
  }

  // Validate --agent values early (applies to both install and check)
  if (effectiveAgentFlag) {
    const { parseAgentList, SkillInstallerError } = await import("./skill-installer.js");
    try {
      parseAgentList(effectiveAgentFlag);
    } catch (error) {
      if (error instanceof SkillInstallerError) {
        return failure(requestId, error.code, error.message, error.nextActions);
      }
      throw error;
    }
  }

  // ─── Check mode ──────────────────────────────────────────────────────

  if (isCheck) {
    const result: Record<string, unknown> = {
      action: "init_check",
      projectPath
    };

    // Check unitypkg status (always, unless --skill-only)
    if (!skillOnly) {
      try {
        const bridge = await checkPackageBridge(projectPath);
        result.package = {
          status: "installed",
          bridge,
          nextActions: ["Unity package com.unity_cli is installed via copy mode in Packages/com.unity_cli/."]
        };
      } catch (error) {
        const { PackageManagerError } = await import("../package-manager.js");
        if (error instanceof PackageManagerError) {
          result.package = {
            status: "not_installed",
            code: error.code,
            message: error.message
          };
        } else {
          throw error;
        }
      }
    }

    // Check skill status for explicit or auto-detected Agent directories.
    if (effectiveAgentFlag) {
      const { agentSkillInstaller, findSkillSourcePath } = await import("./skill-installer.js");
      const sourcePath = await findSkillSourcePath();
      const skillResults = await agentSkillInstaller.checkForAgents(effectiveAgentFlag, sourcePath, projectPath);
      result.skills = skillResults;
    }

    // Check extension auto-detect plan in dry-run check mode.
    if (!skillOnly) {
      const { checkExtensionBundles } = await import("../extensions/installer.js");
      const physicalState = await checkExtensionBundles(projectPath);
      result.extensionPhysicalState = physicalState;
      result.extensionSummary = {
        total: physicalState.length,
        dependencyInstalled: physicalState.filter((item) => item.dependencyInstalled).length,
        installed: physicalState.filter((item) => item.installed).length,
        unhealthy: physicalState.filter((item) => !item.installed || !item.dependencyInstalled).length
      };
      const { autoDetectExtensions } = await import("./extension-auto-detect.js");
      result.extensions = await autoDetectExtensions(projectPath, true);
    }

    return success(requestId, result);
  }

  // ─── Install mode ────────────────────────────────────────────────────

  const baseResult: Record<string, unknown> = {
    action: "init",
    projectPath
  };

  // Install unitypkg (unless --skill-only)
  if (!skillOnly) {
    const bridge = await ensurePackageBridge(projectPath);
    const liveRefresh = await refreshOpenEditor(requestId, flags, runtime);
    baseResult.status = "package_installed";
    baseResult.package = {
      status: "package_installed",
      bridge,
      liveRefresh
    };
    baseResult.liveRefresh = liveRefresh;
    baseResult.nextActions = liveRefresh.status === "triggered"
      ? ["Wait for Unity to finish compiling/importing, then retry /health and Bridge commands against the live endpoint."]
      : ["Open the Unity project to start using the package. The package is copied to Packages/com.unity_cli/."];
  }

  // Install agent skills for explicit or auto-detected Agent directories.
  if (effectiveAgentFlag) {
    const { agentSkillInstaller, findSkillSourcePath } = await import("./skill-installer.js");
    const sourcePath = await findSkillSourcePath();
    const skillResults = await agentSkillInstaller.installForAgents(effectiveAgentFlag, sourcePath, projectPath);
    baseResult.skills = skillResults;

    // Build change description for updated skills
    const updated = skillResults.filter((r) => r.status === "updated");
    if (updated.length > 0) {
      baseResult.skillChanges = updated.map((r) => ({
        agent: r.agent,
        action: "overwritten",
        reason: "source content differs from installed skill",
        previousHash: r.previousHash,
        newHash: r.newHash
      }));
    }
  }

  // Auto-detect and install matching extensions unless skipped by flags.
  if (!skillOnly && !pkgOnly) {
    const { autoDetectExtensions, autoDetectSummary } = await import("./extension-auto-detect.js");
    const detectResult = await autoDetectExtensions(projectPath);
    baseResult.extensions = detectResult;
    baseResult.extensionSummary = autoDetectSummary(detectResult);
  }

  // --skill-only skips package resolution
  if (skillOnly) {
    return success(requestId, baseResult);
  }

  return maybeResolvePackage("init", flags, runtime, requestId, projectPath, baseResult);
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
    const manifest = await installPackage(projectPath, spec);
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "install", package: spec.name, ...manifest });
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
    const manifest = await removePackage(projectPath, name, { force: getBoolean(flags, "force"), dryRun: getBoolean(flags, "dry-run") });
    if (manifest.dryRun) {
      return success(requestId, { action: "remove", package: name, projectPath, ...manifest, status: manifest.blocked ? "blocked" : "dry_run" });
    }
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "remove", package: name, ...manifest });
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
    return success(requestId, { action: "bridge-remove", ...(await removePackageBridge(projectPath, { dryRun: getBoolean(flags, "dry-run") })) });
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
    const deployed = await deployPackageSource(projectPath, source, {
      packageName: getString(flags, "package-name") ?? getString(flags, "packageName") ?? getString(flags, "name"),
      dryRun: getBoolean(flags, "dry-run")
    });
    if (deployed.dryRun) {
      return success(requestId, { action: "deploy", status: "dry_run", ...deployed });
    }
    const liveRefresh = await refreshOpenEditor(requestId, flags, runtime);
    return success(requestId, {
      action: "deploy",
      status: "deployed",
      ...deployed,
      liveRefresh,
      nextActions: liveRefresh.status === "triggered"
        ? ["Wait for Unity to finish importing and compiling, then inspect editor.state or console logs."]
        : ["Open or refresh the Unity project so it imports the deployed package, then inspect console diagnostics."]
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
    const restored = await restoreDeployedPackage(projectPath, packageName, { dryRun: getBoolean(flags, "dry-run") });
    if (restored.dryRun) {
      return success(requestId, { action: "restore", status: "dry_run", ...restored });
    }
    const liveRefresh = await refreshOpenEditor(requestId, flags, runtime);
    return success(requestId, {
      action: "restore",
      status: "restored",
      ...restored,
      liveRefresh,
      nextActions: liveRefresh.status === "triggered"
        ? ["Wait for Unity to finish importing and compiling, then inspect editor.state or console logs."]
        : ["Open or refresh the Unity project so it imports the restored package, then inspect console diagnostics."]
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
  bridgeEndpoint?: string;
}

/** 尝试通知已打开的 Editor 刷新 package 变更。 */
export async function refreshPackageEditor(requestId: string, bridgeEndpoint: string | undefined, runtime: PackageLiveRuntime): Promise<Record<string, unknown>> {
  const endpoint = bridgeEndpoint ?? runtime.bridgeEndpoint ?? process.env.UNITY_CLI_BRIDGE_ENDPOINT;
  if (!endpoint && !runtime.bridgeExecute) {
    return { status: "skipped", reason: "bridge_endpoint_absent" };
  }
  if (endpoint && !runtime.bridgeExecute && !(await isBridgeReachable(endpoint))) {
    return { status: "skipped", reason: "bridge_endpoint_unreachable", endpoint };
  }
  const effectiveEndpoint = endpoint ?? "http://127.0.0.1:47392";
  const result = await executeBridgePayload(`${requestId}_refresh`, "bridge.package.refresh", {}, {
    "bridge-endpoint": effectiveEndpoint,
    "timeout-ms": "15000"
  }, runtime);
  if (result.ok) {
    return { status: "triggered", command: "bridge.package.refresh", endpoint };
  }
  return { status: "failed", command: "bridge.package.refresh", endpoint, code: result.error?.code, message: result.error?.message };
}

export async function refreshOpenEditor(requestId: string, flags: Record<string, string | boolean>, runtime: Runtime): Promise<Record<string, unknown>> {
  return refreshPackageEditor(requestId, getString(flags, "bridge-endpoint"), runtime);
}

async function isBridgeReachable(endpoint: string): Promise<boolean> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 1500);
  try {
    const response = await fetch(new URL("/health", endpoint), { signal: controller.signal });
    return response.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timeout);
  }
}

/** 在 live refresh、跳过和 batch job 之间选择 package resolve 路径。 */
export async function resolvePackageChanges(action: string, options: PackageExecutionOptions, runtime: PackageResolveRuntime, requestId: string, result: Record<string, unknown>): Promise<Record<string, unknown>> {
  const { projectPath } = options;
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  if (!existsSync(manifestPath)) {
    throw new StructuredToolError("MANIFEST_NOT_FOUND", `Unity manifest not found: ${manifestPath}`, [
      "Run the command from a Unity project root or pass --project <path>.",
      "package.resolve requires an existing Packages/manifest.json and does not create Unity projects."
    ], { projectPath, manifestPath, resolve: "blocked_missing_manifest" });
  }

  const liveRefresh = result.liveRefresh as Record<string, unknown> | undefined
    ?? await refreshPackageEditor(requestId, options.bridgeEndpoint, runtime);
  if (liveRefresh.status === "triggered") {
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
      "If the issue persists, close the Unity Editor and retry without --bridge-endpoint.",
      "The manifest has been updated; Unity will detect the change on next domain reload or asset refresh."
    ], { projectPath, resolve: "blocked_by_open_editor", manifestUpdated: true, liveRefresh });
  }

  const unityPath = options.unity ?? discoverUnity();
  if (!unityPath && !options.job) {
    return { ...result, projectPath, status: result.status ?? "manifest_updated", resolve: "skipped", nextActions: result.nextActions ?? ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE to run Unity PackageManager resolve as a job."] };
  }
  if (!unityPath) {
    throw new StructuredToolError("UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }
  if (isProjectLocked(projectPath)) {
    throw new StructuredToolError("UNITY_BUSY", "Cannot start Unity batchmode: the project is currently open in the Unity Editor.", [
      "Close the Unity Editor and retry, or pass --bridge-endpoint <url> to trigger a live refresh.",
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
    timeoutMs: options.timeoutMs,
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
      waitTimeoutMs: Number(getString(flags, "wait-timeout-ms")) || undefined,
      bridgeEndpoint: getString(flags, "bridge-endpoint")
    }, runtime, requestId, result));
  } catch (error) {
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details);
    throw error;
  }
}
