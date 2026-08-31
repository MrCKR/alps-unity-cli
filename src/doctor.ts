/**
 * CLI command: doctor
 *
 * Diagnoses CLI → UnityPkg connectivity, instance health,
 * live Bridge preflight, configuration integrity, and capability discovery.
 * Transport endpoint and listener identity remain internal to instance selection.
 */

import { getString } from "./args.js";
import { access, stat } from "node:fs/promises";
import { discoverProjectTools, type ProjectToolRuntime } from "./project-tool-discovery.js";
import { instanceSelectionFailure, obsoleteLocalStatePaths, readInstances, readInstancesWithRegistryDiagnostics, selectInstanceForFlags, type UnityInstance } from "./instances.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { EDITOR_STATE_OBSERVATION_BUDGET_MS } from "./bridge/session-supervisor.js";
import { builtinToolMetadata } from "./tools/metadata/index.js";
import { builtinExtensionMetadata } from "./extensions/metadata/index.js";
import { ExtensionRegistry, extensionAvailabilitySummary } from "./extensions/index.js";
import { extensionAvailabilityFlags } from "./extension-availability.js";
import { readInstalledUnityPackages } from "./extensions/project-resolver.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export interface DoctorRuntime extends BridgeRuntime, ProjectToolRuntime {
  dataDir: string;
  selectInstance?: typeof selectInstanceForFlags;
}

export interface DoctorCheck {
  name: string;
  status: "pass" | "fail" | "warn";
  message: string;
  details?: Record<string, unknown>;
}

export async function handleDoctorCommand(
  flags: Record<string, string | boolean>,
  runtime: DoctorRuntime,
  requestId: string
): Promise<CliResult> {
  const checks: DoctorCheck[] = [];

  // Check 1: Instance registry
  const instanceCheck = await checkInstanceRegistry(runtime.dataDir);
  checks.push(instanceCheck);

  // Check 2: Reduced v3 instance states
  let readyInstances: UnityInstance[] = [];
  if (instanceCheck.status !== "fail") {
    const instances = await readInstances(runtime.dataDir);
    readyInstances = instances.filter((instance) => instance.status === "ready");
    checks.push(checkReadyInstances(instances, readyInstances));
  } else {
    checks.push({ name: "ready_instances", status: "warn", message: "Skipped: instance registry not available." });
  }

  // Check 3: Bridge connectivity follows the same strict --instance/CWD selector as business calls.
  let bridgeReachable = false;
  try {
    const selection = await (runtime.selectInstance ?? selectInstanceForFlags)(flags, runtime.dataDir);
    const bridgeCheck = await checkBridgeConnectivity(selection.instance.instanceId, requestId, runtime);
    checks.push(bridgeCheck);
    bridgeReachable = bridgeCheck.status === "pass";
  } catch (error) {
    const selectionFailure = instanceSelectionFailure(requestId, error);
    const selectionError = selectionFailure && !selectionFailure.ok ? selectionFailure.error : undefined;
    checks.push({
      name: "bridge_connectivity",
      status: "warn",
      message: selectionError?.message ?? "No Unity project identity can be selected from --instance or CWD.",
      details: selectionError ? { code: selectionError.code, details: selectionError.details } : undefined,
    });
  }

  // Check 4: Data directory
  checks.push(await checkDataDirectory(runtime.dataDir));

  // Check 5: Configuration integrity and obsolete local state
  checks.push(await checkConfigIntegrity(readyInstances));
  checks.push(await checkObsoleteLocalState(runtime.dataDir));

  const fingerprintCheck = checkBridgeFingerprintStatus(checks);
  if (fingerprintCheck) {
    checks.push(fingerprintCheck);
  }

  const compileCheck = checkCompileStatus(checks);
  if (compileCheck) {
    checks.push(compileCheck);
  }

  // Unified CLI/tools discovery surface
  checks.push(await checkUnifiedDiscovery(flags, runtime, requestId, bridgeReachable));

  // Extension package availability comes from physical project state.
  checks.push(await checkExtensionAvailability(flags, runtime));

  checks.push(checkExtensionRegistry());

  const allPassed = checks.every((c) => c.status === "pass");
  const hasWarnings = checks.some((c) => c.status === "warn");
  const hasFailures = checks.some((c) => c.status === "fail");

  const summary = hasFailures ? "fail" : hasWarnings ? "warn" : "pass";

  const data: Record<string, unknown> = {
    summary,
    checks,
    suggestions: buildSuggestions(checks)
  };

  return success(requestId, data);
}

async function checkInstanceRegistry(dataDir: string): Promise<DoctorCheck> {
  try {
    const { instances, invalidRecordCount } = await readInstancesWithRegistryDiagnostics(dataDir);
    if (invalidRecordCount > 0) {
      return {
        name: "instance_registry",
        status: "warn",
        message: `Instance registry contains ${instances.length} valid instance(s) and ignored ${invalidRecordCount} invalid or unreadable record(s).`,
        details: { instanceCount: instances.length, invalidRecordCount },
      };
    }
    if (instances.length === 0) {
      return {
        name: "instance_registry",
        status: "warn",
        message: "Instance registry is empty. No Unity instances registered.",
        details: { instanceCount: 0 }
      };
    }
    return {
      name: "instance_registry",
      status: "pass",
      message: `Instance registry contains ${instances.length} instance(s).`,
      details: { instanceCount: instances.length }
    };
  } catch (error) {
    return {
      name: "instance_registry",
      status: "fail",
      message: `Failed to read instance registry directory: ${error instanceof Error ? error.message : String(error)}`
    };
  }
}

function checkReadyInstances(instances: UnityInstance[], ready: UnityInstance[]): DoctorCheck {
  const conflicts = instances.filter((instance) => instance.status === "conflict");
  const unverifiable = instances.filter((instance) => instance.status === "unverifiable");
  const blocked = instances.filter((instance) => instance.status === "blocked");
  if (conflicts.length > 0) {
    return {
      name: "ready_instances",
      status: "fail",
      message: `${conflicts.length} project identity conflict(s) block live dispatch.`,
      details: { readyCount: ready.length, conflictCount: conflicts.length, blockedCount: blocked.length }
    };
  }
  if (unverifiable.length > 0) {
    return {
      name: "ready_instances",
      status: "fail",
      message: `${unverifiable.length} project identity observation(s) could not be verified safely.`,
      details: { readyCount: ready.length, conflictCount: 0, unverifiableCount: unverifiable.length, blockedCount: blocked.length }
    };
  }
  if (ready.length === 0) {
    return {
      name: "ready_instances",
      status: "warn",
      message: "No ready Unity Editor session found.",
      details: {
        readyCount: 0,
        transitioningCount: instances.filter((instance) => instance.status === "transitioning").length,
        unresponsiveCount: instances.filter((instance) => instance.status === "unresponsive").length,
        blockedCount: blocked.length,
        offlineCount: instances.filter((instance) => instance.status === "offline").length,
      }
    };
  }
  return {
    name: "ready_instances",
    status: "pass",
    message: `${ready.length} ready instance(s) found.`,
    details: {
      readyCount: ready.length,
      instances: ready.map((instance) => ({
        instanceId: instance.instanceId,
        canonicalProjectPath: instance.canonicalProjectPath,
        editorSessionId: instance.editorSessionId,
        publishedAt: instance.publishedAt,
      }))
    }
  };
}

async function checkBridgeConnectivity(
  instanceId: string,
  requestId: string,
  runtime: BridgeRuntime
): Promise<DoctorCheck> {
  try {
    const result = await executeBridgePayload(
      requestId,
      "project-tool.run",
      { name: "diagnose.preflight", input: {} },
      { instance: instanceId, "timeout-ms": String(EDITOR_STATE_OBSERVATION_BUDGET_MS) },
      runtime,
      undefined,
      // connectivity probe 与 editor.state 共享同一即时观察预算，由 supervisor 将 Bridge POST 限制为 3500ms。
      { mode: "observeNow", mutation: false, defaultTimeoutMs: EDITOR_STATE_OBSERVATION_BUDGET_MS }
    );

    if (result.ok) {
      const wrapperData = result.data;
      const bridgeResponse = wrapperData && typeof wrapperData === "object" && !Array.isArray(wrapperData)
        && (wrapperData as Record<string, unknown>).result && typeof (wrapperData as Record<string, unknown>).result === "object"
        ? (wrapperData as Record<string, unknown>).result
        : wrapperData;
      return {
        name: "bridge_connectivity",
        status: "pass",
        message: `Bridge identity is reachable for ${instanceId}.`,
        details: { instanceId, bridgeResponse }
      };
    }

    return {
      name: "bridge_connectivity",
      status: "fail",
      message: `Bridge for ${instanceId} returned error: ${result.error?.code ?? "unknown"} — ${result.error?.message ?? "no message"}`,
      details: { instanceId, errorCode: result.error?.code, errorMessage: result.error?.message }
    };
  } catch (error) {
    return {
      name: "bridge_connectivity",
      status: "fail",
      message: `Cannot reach bridge for ${instanceId}: ${error instanceof Error ? error.message : String(error)}`,
      details: { instanceId, error: error instanceof Error ? error.message : String(error) }
    };
  }
}

function checkBridgeFingerprintStatus(checks: DoctorCheck[]): DoctorCheck | undefined {
  const bridgeCheck = checks.find((check) => check.name === "bridge_connectivity" && check.status === "pass");
  const bridgeResponse = (bridgeCheck?.details as { bridgeResponse?: unknown } | undefined)?.bridgeResponse;
  if (!bridgeResponse || typeof bridgeResponse !== "object") {
    return undefined;
  }
  const customTools = (bridgeResponse as { customTools?: unknown }).customTools;
  const fingerprint = customTools && typeof customTools === "object" ? (customTools as { fingerprint?: unknown }).fingerprint : undefined;
  if (!fingerprint || typeof fingerprint !== "object") {
    return undefined;
  }
  const blockedCount = numberProp(fingerprint, "blockedCount") ?? 0;
  const toolCount = numberProp(fingerprint, "toolCount") ?? 0;
  const status: DoctorCheck["status"] = blockedCount > 0 ? "fail" : "pass";
  return {
    name: "project_tool_fingerprint",
    status,
    message: status === "pass"
      ? `Custom tool fingerprints match for ${toolCount} tool(s).`
      : `Custom tool fingerprints are blocked for ${blockedCount} of ${toolCount} tool(s).`,
    details: { fingerprint }
  };
}

function checkCompileStatus(checks: DoctorCheck[]): DoctorCheck | undefined {
  const bridgeCheck = checks.find((check) => check.name === "bridge_connectivity" && check.status === "pass");
  const bridgeResponse = (bridgeCheck?.details as { bridgeResponse?: unknown } | undefined)?.bridgeResponse;
  if (!bridgeResponse || typeof bridgeResponse !== "object") {
    return undefined;
  }
  const checksArray = (bridgeResponse as { checks?: unknown }).checks;
  if (!Array.isArray(checksArray)) {
    return undefined;
  }
  const compile = checksArray.find((entry): entry is Record<string, unknown> =>
    typeof entry === "object" && entry !== null && (entry as Record<string, unknown>).name === "compileStatus");
  if (!compile) {
    return undefined;
  }
  const compiling = compile.compiling === true;
  const hasErrors = compile.hasCompileErrors === true;
  const errors = Array.isArray(compile.compileErrors) ? compile.compileErrors.filter((e): e is string => typeof e === "string") : [];
  const status: DoctorCheck["status"] = hasErrors ? "fail" : compiling ? "warn" : "pass";
  const message = hasErrors
    ? `Unity has compile errors (${errors.length} shown). Often caused by a broken extension under Packages/com.alpsckr.unitycli/Extensions.`
    : compiling
      ? "Unity is currently compiling scripts."
      : "Unity scripts compiled cleanly.";
  return {
    name: "compile_status",
    status,
    message,
    details: { compiling, hasCompileErrors: hasErrors, compileErrors: errors.slice(0, 10), nextActions: compile.nextActions }
  };
}

function numberProp(value: unknown, key: string): number | undefined {
  const raw = value && typeof value === "object" ? (value as Record<string, unknown>)[key] : undefined;
  return typeof raw === "number" && Number.isFinite(raw) ? raw : undefined;
}

function stringProp(value: unknown, key: string): string | undefined {
  const raw = value && typeof value === "object" ? (value as Record<string, unknown>)[key] : undefined;
  return typeof raw === "string" ? raw : undefined;
}

async function checkDataDirectory(dataDir: string): Promise<DoctorCheck> {
  try {
    await access(dataDir);
    const stats = await stat(dataDir);
    if (!stats.isDirectory()) {
      return { name: "data_directory", status: "fail", message: `Data path is not a directory: ${dataDir}` };
    }
    return {
      name: "data_directory",
      status: "pass",
      message: `Data directory exists: ${dataDir}`,
      details: { dataDir }
    };
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT") {
      return {
        name: "data_directory",
        status: "warn",
        message: `Data directory does not exist yet: ${dataDir}. It will be created on first use.`,
        details: { dataDir }
      };
    }
    return {
      name: "data_directory",
      status: "fail",
      message: `Cannot access data directory: ${error instanceof Error ? error.message : String(error)}`
    };
  }
}

async function checkUnifiedDiscovery(flags: Record<string, string | boolean>, runtime: DoctorRuntime, requestId: string, bridgeReachable: boolean): Promise<DoctorCheck> {
  if (!bridgeReachable && runtime.projectTools === undefined) {
    return {
      name: "unified_tool_discovery",
      status: "warn",
      message: "Tool discovery is unavailable because no Unity instance is connected.",
      details: { mode: "unavailable", toolCount: 0 }
    };
  }
  const discovery = await discoverProjectTools(flags, runtime, `${requestId}_project_tools`, { requireBridge: true });
  if (!discovery.ok) {
    return {
      name: "unified_tool_discovery",
      status: "warn",
      message: `Tool discovery failed: ${discovery.error?.code ?? "unknown"} — ${discovery.error?.message ?? "no message"}`,
      details: { mode: "error", toolCount: 0, errorCode: discovery.error?.code, errorMessage: discovery.error?.message }
    };
  }

  const localCount = builtinToolMetadata().length;
  return {
    name: "unified_tool_discovery",
    status: "pass",
    message: `The connected instance exposes ${localCount + discovery.tools.length} Tool descriptor(s).`,
    details: { mode: discovery.mode, localCount, liveCount: discovery.tools.length, toolCount: localCount + discovery.tools.length }
  };
}

async function checkConfigIntegrity(readyInstances: UnityInstance[]): Promise<DoctorCheck> {
  const issues: string[] = [];

  for (const instance of readyInstances) {
    if (instance.canonicalizationVersion !== 1) issues.push(`Instance ${instance.instanceId}: unsupported canonicalizationVersion.`);
    if (!/^unity-[0-9a-f]{64}$/.test(instance.instanceId)) issues.push(`Instance ${instance.instanceId}: invalid deterministic instanceId.`);
    if (!instance.canonicalProjectPath) issues.push(`Instance ${instance.instanceId}: missing canonicalProjectPath.`);
    if (!instance.editorSessionId) issues.push(`Instance ${instance.instanceId}: missing editorSessionId.`);
    if (!instance.publishedAt) issues.push(`Instance ${instance.instanceId}: missing publishedAt.`);
  }

  if (issues.length > 0) {
    return {
      name: "config_integrity",
      status: "warn",
      message: `${issues.length} configuration issue(s) found.`,
      details: { issues }
    };
  }

  return {
    name: "config_integrity",
    status: "pass",
    message: readyInstances.length > 0
      ? `${readyInstances.length} instance(s) passed v3 identity checks.`
      : "No ready instances to check.",
    details: { checkedInstances: readyInstances.length }
  };
}

/** 旧 active-target/alias/aggregate 文件只报告，不解析、不迁移。 */
async function checkObsoleteLocalState(dataDir: string): Promise<DoctorCheck> {
  const paths = await obsoleteLocalStatePaths(dataDir);
  return paths.length === 0
    ? { name: "obsolete_local_state", status: "pass", message: "No obsolete target or registry state files found." }
    : {
        name: "obsolete_local_state",
        status: "fail",
        message: "E_OBSOLETE_LOCAL_STATE: obsolete active-target or registry files require manual deletion.",
        details: { code: "E_OBSOLETE_LOCAL_STATE", paths },
      };
}

async function checkExtensionAvailability(flags: Record<string, string | boolean>, runtime: { dataDir?: string }): Promise<DoctorCheck> {
  const resolvedFlags = await extensionAvailabilityFlags(flags, runtime);
  const projectPath = getString(resolvedFlags, "project");
  if (!projectPath) {
    return {
      name: "extension_availability",
      status: "warn",
      message: "No project path provided. Cannot resolve extension availability. Pass --project <path>.",
      details: { projectResolved: false }
    };
  }

  const registry = new ExtensionRegistry();
  let installedPackages: Map<string, string>;
  try {
    installedPackages = await readInstalledUnityPackages(projectPath);
  } catch {
    return {
      name: "extension_availability",
      status: "warn",
      message: `Cannot read Unity packages from project: ${projectPath}.`,
      details: { projectPath }
    };
  }

  const summary = extensionAvailabilitySummary(registry, installedPackages);

  const unavailableExtensions = summary.extensions.filter((e) => e.status === "unavailable");
  const unknownExtensions = summary.extensions.filter((e) => e.status === "unknown");

  if (unavailableExtensions.length > 0) {
    return {
      name: "extension_availability",
      status: "fail",
      message: `${unavailableExtensions.length} extension(s) have missing Unity package dependencies: ${unavailableExtensions.map((e) => e.name + " (missing: " + e.missingPackages.join(", ") + ")").join("; ")}.`,
      details: {
        projectPath,
        projectResolved: true,
        total: summary.total,
        available: summary.available,
        unavailable: summary.unavailable,
        unknown: summary.unknown,
        unavailableExtensions,
        unknownExtensions
      }
    };
  }

  if (unknownExtensions.length > 0) {
    return {
      name: "extension_availability",
      status: "warn",
      message: `${unknownExtensions.length} extension(s) could not be resolved (no installed packages data): ${unknownExtensions.map((e) => e.name).join(", ")}.`,
      details: {
        projectPath,
        projectResolved: true,
        total: summary.total,
        available: summary.available,
        unavailable: summary.unavailable,
        unknown: summary.unknown
      }
    };
  }

  return {
    name: "extension_availability",
    status: "pass",
    message: `All ${summary.total} extensions resolved: ${summary.available} available, ${summary.unavailable} unavailable, ${summary.unknown} unknown.`,
    details: {
      projectPath,
      projectResolved: true,
      total: summary.total,
      available: summary.available,
      unavailable: summary.unavailable,
      unknown: summary.unknown,
      extensions: summary.extensions
    }
  };
}

function checkExtensionRegistry(): DoctorCheck {
  const extensions = builtinExtensionMetadata();
  const installed = extensions.filter((e) => e.status === "installed").length;
  const available = extensions.filter((e) => e.status === "available").length;
  return {
    name: "extension_registry",
    status: "pass",
    message: `Extension registry loaded: ${extensions.length} extension(s) (${installed} installed, ${available} available).`,
    details: { total: extensions.length, installed, available }
  };
}

function buildSuggestions(checks: DoctorCheck[]): string[] {
  const suggestions: string[] = [];

  for (const check of checks) {
    if (check.status === "fail" || check.status === "warn") {
      switch (check.name) {
        case "instance_registry":
          if (check.status === "warn") {
            suggestions.push("Open a Unity project with the UnityCLI Bridge package installed.");
            suggestions.push("Run unitycli init package --project <UnityProject> to set up the Bridge.");
          }
          break;
        case "ready_instances":
          suggestions.push("Reopen the Unity Editor for the exact canonical project path if no session is active.");
          suggestions.push("Run unitycli instances list to inspect transitioning, blocked, offline, or conflicting claims.");
          break;
        case "bridge_connectivity":
          suggestions.push("Ensure Unity Editor is running with the UnityCLI Bridge package for the selected --instance or CWD project.");
          suggestions.push("Run unitycli instances list to inspect identity, session, blocker, and transition state.");
          break;
        case "project_tool_fingerprint":
          suggestions.push("Inspect unitycli doctor fingerprint details, then review project tool descriptor/handler code changes.");
          suggestions.push("Apply the reported rePinValue only after accepting the exact Tool change.");
          break;
        case "data_directory":
          suggestions.push("Run any unitycli command to auto-create the data directory.");
          break;
        case "config_integrity":
          suggestions.push("Re-initialize the canonical registry claim by restarting Unity Editor.");
          break;
        case "obsolete_local_state":
          suggestions.push("Delete only the reported obsolete config.json, aliases.json, or aggregate instances.json files after auditing them.");
          break;
        case "unified_tool_discovery":
          suggestions.push("Run unitycli tools group to see tool groups.");
          suggestions.push("Run unitycli tools group list <group> with a connected Unity instance to inspect its complete Tool descriptors.");
          break;
        case "extension_registry":
          suggestions.push("Run unitycli extensions list to see available extensions.");
          suggestions.push("Run unitycli extensions doctor to check extension status.");
          break;
        case "extension_availability":
          suggestions.push("Run 'unitycli extensions doctor --project <path>' for detailed extension diagnostics.");
          suggestions.push("Run 'unitycli extensions list --project <path>' to see all extensions and their status.");
          suggestions.push("Install missing Unity packages manually via Unity Package Manager or Packages/manifest.json, then run the matching 'unitycli extensions install <id> --project <path>'.");
          break;
      }
    }
  }

  if (suggestions.length === 0) {
    suggestions.push("All checks passed. No action needed.");
  }

  return suggestions;
}
