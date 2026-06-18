import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { executeBridgePayload } from "../bridge/index.js";
import { confirmDestructiveExecution } from "../security.js";
import { failure, success } from "../output.js";
import { handlePackageInfoCommand } from "../package-info.js";
import { discoverUnity } from "../platform.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { startUnityJob } from "./unity-job.js";

export async function handleInit(flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const { checkPackageBridge, ensurePackageBridge } = await import("../package-manager.js");
  const agentFlag = getString(flags, "agent");

  if (getBoolean(flags, "check")) {
    const bridge = await checkPackageBridge(projectPath);
    const result: Record<string, unknown> = {
      action: "init_check",
      status: "installed",
      projectPath,
      bridge,
      nextActions: ["Unity package com.unity_cli is installed via copy mode in Packages/com.unity_cli/."]
    };

    // When --agent is specified with --check, also check agent skill status
    if (agentFlag) {
      const { agentSkillInstaller, findSkillSourcePath } = await import("./skill-installer.js");
      const sourcePath = await findSkillSourcePath();
      const skillResults = await agentSkillInstaller.checkForAgents(agentFlag, sourcePath, projectPath);
      result.skills = skillResults;
    }

    return success(requestId, result);
  }

  const bridge = await ensurePackageBridge(projectPath);
  const liveRefresh = await refreshOpenEditor(requestId, flags, runtime);
  const baseResult: Record<string, unknown> = {
    action: "init",
    status: "package_installed",
    bridge,
    liveRefresh,
    nextActions: liveRefresh.status === "triggered"
      ? ["Wait for Unity to finish compiling/importing, then retry /health and Bridge commands against the live endpoint."]
      : ["Open the Unity project to start using the package. The package is copied to Packages/com.unity_cli/."]
  };

  // When --agent is specified, install skills for the listed agents
  if (agentFlag) {
    const { agentSkillInstaller, findSkillSourcePath } = await import("./skill-installer.js");
    const sourcePath = await findSkillSourcePath();
    const skillResults = await agentSkillInstaller.installForAgents(agentFlag, sourcePath, projectPath);
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

  return maybeResolvePackage("init", flags, runtime, requestId, projectPath, baseResult);
}

export async function handlePackage(command: string | undefined, args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const {
    addScopedRegistry,
    clearRegistry,
    installPackage,
    packageInfo,
    parsePackageSpec,
    parseScopes,
    readSnapshot,
    removePackage,
    removeScopedRegistry,
    searchRegistry,
    setRegistry,
    validatePackageName,
    validateRegistryUrl
  } = await import("../package-manager.js");

  if (!command) {
    return handlePackageInfoCommand(flags, requestId);
  }

  if (command === "list") {
    return success(requestId, await readSnapshot(projectPath));
  }

  if (command === "info") {
    const name = validatePackageName(args[0] ?? getString(flags, "name"));
    const info = await packageInfo(projectPath, name);
    return info ? success(requestId, { projectPath, package: info }) : failure(requestId, "PACKAGE_NOT_FOUND", `Package not found: ${name}`, ["Run unity_cli package list to inspect installed packages."]);
  }

  if (command === "search") {
    const query = validatePackageName(args[0] ?? getString(flags, "query"));
    const registry = validateRegistryUrl(getString(flags, "registry") ?? (await readSnapshot(projectPath)).registry ?? "https://packages.unity.com");
    return success(requestId, { registry, query, packages: await searchRegistry(registry, query) });
  }

  if (command === "install") {
    const spec = parsePackageSpec(validatePackageName(args[0] ?? getString(flags, "name")), getString(flags, "version"));
    const manifest = await installPackage(projectPath, spec);
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "install", package: spec.name, ...manifest });
  }

  if (command === "remove") {
    const name = validatePackageName(args[0] ?? getString(flags, "name"));
    const confirmation = await confirmDestructiveExecution(flags, {
      command: "cli.package.remove",
      title: "Package Remove",
      destructiveHint: true,
      requestId
    });
    if (confirmation) {
      return confirmation;
    }
    const manifest = await removePackage(projectPath, name);
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "remove", package: name, ...manifest });
  }

  if (command === "resolve") {
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "resolve" });
  }

  if (command === "registry") {
    const action = args[0] ?? getString(flags, "action") ?? "get";
    if (action === "get") {
      const snapshot = await readSnapshot(projectPath);
      return success(requestId, { projectPath, manifestPath: snapshot.manifestPath, registry: snapshot.registry });
    }
    if (action === "set") {
      return success(requestId, { projectPath, ...(await setRegistry(projectPath, validateRegistryUrl(getString(flags, "url") ?? args[1]))) });
    }
    if (action === "clear") {
      return success(requestId, { projectPath, ...(await clearRegistry(projectPath)) });
    }
    return failure(requestId, "UNKNOWN_PACKAGE_REGISTRY_COMMAND", "Expected package registry get, set or clear.");
  }

  if (command === "scoped-registry") {
    const action = args[0] ?? getString(flags, "action") ?? "list";
    if (action === "list") {
      const snapshot = await readSnapshot(projectPath);
      return success(requestId, { projectPath, manifestPath: snapshot.manifestPath, scopedRegistries: snapshot.scopedRegistries });
    }
    if (action === "add") {
      const name = validatePackageName(getString(flags, "name") ?? args[1]);
      const url = validateRegistryUrl(getString(flags, "url"));
      const scopes = parseScopes(getString(flags, "scope") ?? getString(flags, "scopes"));
      return success(requestId, { projectPath, ...(await addScopedRegistry(projectPath, { name, url, scopes })) });
    }
    if (action === "remove") {
      const name = validatePackageName(getString(flags, "name") ?? args[1]);
      return success(requestId, { projectPath, ...(await removeScopedRegistry(projectPath, name)) });
    }
    return failure(requestId, "UNKNOWN_SCOPED_REGISTRY_COMMAND", "Expected package scoped-registry list, add or remove.");
  }

  return failure(requestId, "UNKNOWN_PACKAGE_COMMAND", "Expected package list, info, search, install, remove, resolve, registry or scoped-registry.");
}

async function refreshOpenEditor(requestId: string, flags: Record<string, string | boolean>, runtime: Runtime): Promise<Record<string, unknown>> {
  const endpoint = getString(flags, "bridge-endpoint") ?? runtime.bridgeEndpoint ?? process.env.UNITY_CLI_BRIDGE_ENDPOINT ?? "http://127.0.0.1:47392";
  if (!runtime.bridgeExecute && !(await isBridgeReachable(endpoint))) {
    return { status: "skipped", reason: "bridge_endpoint_absent", endpoint };
  }
  const result = await executeBridgePayload(`${requestId}_refresh`, "editor.menu", { path: "Assets/Refresh", timeoutMs: 7000 }, { ...flags, "bridge-endpoint": endpoint, __destructiveConfirmed: true }, runtime);
  if (result.ok) {
    return { status: "triggered", command: "editor.menu", path: "Assets/Refresh", endpoint };
  }
  return { status: "failed", command: "editor.menu", path: "Assets/Refresh", endpoint, code: result.error?.code, message: result.error?.message };
}

function isProjectLocked(projectPath: string): boolean {
  return existsSync(resolve(projectPath, "Temp", "UnityLockfile"));
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

async function maybeResolvePackage(command: string, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, projectPath: string, result: Record<string, unknown>): Promise<CliResult> {
  // If a live Editor is reachable via bridge, trigger an asset refresh so it picks up the manifest change.
  // Skip if caller already performed a bridge refresh (e.g. handleInit).
  let liveRefresh = result.liveRefresh as Record<string, unknown> | undefined;
  if (!liveRefresh) {
    liveRefresh = await refreshOpenEditor(requestId, flags, runtime);
  }

  // If bridge refresh was triggered, return success — no need for Unity path or batchmode.
  if (liveRefresh.status === "triggered") {
    return success(requestId, {
      ...result,
      projectPath,
      status: result.status ?? "manifest_updated",
      resolve: "live_bridge_refresh",
      liveRefresh,
      nextActions: result.nextActions ?? ["The open Unity Editor has been notified to refresh assets. Wait for Unity to finish reimporting before using the package."]
    });
  }

  // If bridge was reachable but the refresh command failed, the Editor is open — batchmode would also fail.
  if (liveRefresh.status === "failed") {
    return failure(requestId, "UNITY_BUSY", "The Unity Editor is open but the asset refresh command failed.", [
      "Wait for Unity to finish compiling/importing and retry.",
      "If the issue persists, close the Unity Editor and retry without --bridge-endpoint.",
      "The manifest has been updated; Unity will detect the change on next domain reload or asset refresh."
    ], {
      projectPath,
      resolve: "blocked_by_open_editor",
      manifestUpdated: true,
      liveRefresh
    });
  }

  // Bridge was not reachable — fall through to Unity path / batchmode checks.
  const unityPath = getString(flags, "unity") ?? discoverUnity();
  if (!unityPath && !getBoolean(flags, "job")) {
    return success(requestId, { ...result, projectPath, status: result.status ?? "manifest_updated", resolve: "skipped", nextActions: result.nextActions ?? ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE to run Unity PackageManager resolve as a job."] });
  }
  if (!unityPath) {
    return failure(requestId, "UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }

  // Pre-flight: check if the project is locked by another Unity instance (batchmode will fail if so).
  if (isProjectLocked(projectPath)) {
    return failure(requestId, "UNITY_BUSY", "Cannot start Unity batchmode: the project is currently open in the Unity Editor.", [
      "Close the Unity Editor and retry, or pass --bridge-endpoint <url> to trigger a live refresh.",
      "The manifest has been updated; Unity will detect the change on next domain reload or asset refresh.",
      "If Unity is not running, delete the stale lock file: <project>/Temp/UnityLockfile"
    ], {
      projectPath,
      resolve: "blocked_by_open_editor",
      manifestUpdated: true
    });
  }

  const { ensurePackageBridge } = await import("../package-manager.js");
  const bridge = await ensurePackageBridge(projectPath);
  return startUnityJob("package", flags, runtime, requestId, ["-executeMethod", "UnityCli.UnityCliBatch.ResolvePackages", "-unityCliPackageAction", command], { ...result, bridge });
}
