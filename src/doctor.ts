/**
 * CLI command: doctor
 *
 * Diagnoses CLI → UnityPkg connectivity, instance health,
 * bridge status, and configuration integrity.
 *
 * Checks:
 * 1. Instance registry readable and contains entries
 * 2. At least one healthy instance (non-stale heartbeat)
 * 3. Bridge endpoint reachable (/health endpoint)
 * 4. Bridge capabilities endpoint returns data
 * 5. Job store readable
 * 6. Data directory exists
 */

import { getString, getBoolean } from "./args.js";
import { discoverProjectTools, shouldDiscoverProjectTools, type ProjectToolRuntime } from "./project-tool-discovery.js";
import { discoverProjectContexts, shouldDiscoverContexts, type ProjectContextRuntime, type ContextDescriptor } from "./project-context-discovery.js";
import { readInstances, type UnityInstance } from "./instances.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { builtinToolMetadata } from "./tools/metadata/index.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export interface DoctorRuntime extends BridgeRuntime, ProjectToolRuntime, ProjectContextRuntime {
  dataDir: string;
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
  const jsonFormat = getBoolean(flags, "json");

  // Check 1: Instance registry
  const instanceCheck = await checkInstanceRegistry(runtime.dataDir);
  checks.push(instanceCheck);

  // Check 2: Healthy instances
  let healthyInstances: UnityInstance[] = [];
  if (instanceCheck.status !== "fail") {
    const instances = await readInstances(runtime.dataDir);
    healthyInstances = instances.filter((i) => i.status === "healthy" || i.status === "ready");
    checks.push(checkHealthyInstances(instances, healthyInstances));
  } else {
    checks.push({ name: "healthy_instances", status: "warn", message: "Skipped: instance registry not available." });
  }

  // Check 3: Bridge connectivity
  const endpoint = getString(flags, "bridge-endpoint") ?? runtime.bridgeEndpoint ?? process.env.UNITY_CLI_BRIDGE_ENDPOINT;
  if (healthyInstances.length > 0 && !endpoint) {
    checks.push(await checkBridgeConnectivity(healthyInstances[0].bridgeEndpoint, requestId, runtime));
  } else if (endpoint) {
    checks.push(await checkBridgeConnectivity(endpoint, requestId, runtime));
  } else {
    checks.push({ name: "bridge_connectivity", status: "warn", message: "No bridge endpoint available. Open Unity or pass --bridge-endpoint." });
  }

  // Check 4: Data directory
  checks.push(await checkDataDirectory(runtime.dataDir));

  // Check 5: Job store
  checks.push(await checkJobStore(runtime.dataDir));

  // Check 6: Configuration integrity
  checks.push(await checkConfigIntegrity(healthyInstances));

  const fingerprintCheck = checkBridgeFingerprintStatus(checks);
  if (fingerprintCheck) {
    checks.push(fingerprintCheck);
  }

  // Unified CLI/tools discovery surface
  checks.push(await checkUnifiedDiscovery(flags, runtime, requestId));

  // Custom context discovery
  checks.push(await checkContextDiscovery(flags, runtime, requestId));

  // Custom context fingerprint
  const contextFingerprintCheck = checkContextFingerprintStatus(checks);
  if (contextFingerprintCheck) {
    checks.push(contextFingerprintCheck);
  }

  const allPassed = checks.every((c) => c.status === "pass");
  const hasWarnings = checks.some((c) => c.status === "warn");
  const hasFailures = checks.some((c) => c.status === "fail");

  const summary = hasFailures ? "fail" : hasWarnings ? "warn" : "pass";

  const data: Record<string, unknown> = {
    command: "doctor",
    summary,
    checks,
    suggestions: buildSuggestions(checks)
  };

  return success(requestId, data);
}

async function checkInstanceRegistry(dataDir: string): Promise<DoctorCheck> {
  try {
    const instances = await readInstances(dataDir);
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
      message: `Failed to read instance registry: ${error instanceof Error ? error.message : String(error)}`
    };
  }
}

function checkHealthyInstances(instances: UnityInstance[], healthy: UnityInstance[]): DoctorCheck {
  const stale = instances.filter((i) => i.status === "stale");
  if (healthy.length === 0 && stale.length > 0) {
    return {
      name: "healthy_instances",
      status: "fail",
      message: `All ${stale.length} registered instance(s) are stale. Unity may be closed.`,
      details: { healthyCount: 0, staleCount: stale.length }
    };
  }
  if (healthy.length === 0) {
    return {
      name: "healthy_instances",
      status: "warn",
      message: "No healthy instances found.",
      details: { healthyCount: 0, staleCount: 0 }
    };
  }
  return {
    name: "healthy_instances",
    status: "pass",
    message: `${healthy.length} ready instance(s) found.`,
    details: {
      healthyCount: healthy.length,
      staleCount: stale.length,
      instances: healthy.map((i) => ({
        instanceId: i.instanceId,
        projectPath: i.projectPath,
        bridgeEndpoint: i.bridgeEndpoint,
        lastHeartbeatAt: i.lastHeartbeatAt ?? i.lastSeenAt
      }))
    }
  };
}

async function checkBridgeConnectivity(
  endpoint: string | undefined,
  requestId: string,
  runtime: BridgeRuntime
): Promise<DoctorCheck> {
  if (!endpoint) {
    return { name: "bridge_connectivity", status: "warn", message: "No bridge endpoint configured." };
  }

  try {
    const result = await executeBridgePayload(
      requestId,
      "diagnose.preflight",
      {},
      { "bridge-endpoint": endpoint },
      runtime
    );

    if (result.ok) {
      return {
        name: "bridge_connectivity",
        status: "pass",
        message: `Bridge reachable at ${endpoint}.`,
        details: { endpoint, bridgeResponse: result.data }
      };
    }

    return {
      name: "bridge_connectivity",
      status: "fail",
      message: `Bridge at ${endpoint} returned error: ${result.error?.code ?? "unknown"} — ${result.error?.message ?? "no message"}`,
      details: { endpoint, errorCode: result.error?.code, errorMessage: result.error?.message }
    };
  } catch (error) {
    return {
      name: "bridge_connectivity",
      status: "fail",
      message: `Cannot reach bridge at ${endpoint}: ${error instanceof Error ? error.message : String(error)}`,
      details: { endpoint, error: error instanceof Error ? error.message : String(error) }
    };
  }
}

function checkBridgeFingerprintStatus(checks: DoctorCheck[]): DoctorCheck | undefined {
  const bridgeCheck = checks.find((check) => check.name === "bridge_connectivity" && check.status === "pass");
  const bridgeResponse = (bridgeCheck?.details as { bridgeResponse?: unknown } | undefined)?.bridgeResponse;
  if (!bridgeResponse || typeof bridgeResponse !== "object") {
    return undefined;
  }
  const projectTools = (bridgeResponse as { projectTools?: unknown }).projectTools;
  const fingerprint = projectTools && typeof projectTools === "object" ? (projectTools as { fingerprint?: unknown }).fingerprint : undefined;
  if (!fingerprint || typeof fingerprint !== "object") {
    return undefined;
  }
  const blockedCount = numberProp(fingerprint, "blockedCount") ?? 0;
  const warnCount = numberProp(fingerprint, "warnCount") ?? 0;
  const toolCount = numberProp(fingerprint, "toolCount") ?? 0;
  const mode = stringProp(fingerprint, "mode") ?? "unknown";
  const status: DoctorCheck["status"] = blockedCount > 0 ? "fail" : warnCount > 0 ? "warn" : "pass";
  return {
    name: "project_tool_fingerprint",
    status,
    message: status === "pass"
      ? `Custom tool fingerprints match for ${toolCount} tool(s) (mode: ${mode}).`
      : `Custom tool fingerprint issues: ${blockedCount} blocked, ${warnCount} warning(s) (mode: ${mode}).`,
    details: { fingerprint }
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
    const { access, stat } = await import("node:fs/promises");
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

async function checkJobStore(dataDir: string): Promise<DoctorCheck> {
  try {
    const { readFile } = await import("node:fs/promises");
    const { join } = await import("node:path");
    const jobStorePath = join(dataDir, "jobs.json");
    const raw = await readFile(jobStorePath, "utf8");
    const parsed = JSON.parse(raw) as { jobs?: unknown[] };
    const jobCount = Array.isArray(parsed.jobs) ? parsed.jobs.length : 0;
    return {
      name: "job_store",
      status: "pass",
      message: `Job store contains ${jobCount} job(s).`,
      details: { jobCount, jobStorePath }
    };
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT") {
      return {
        name: "job_store",
        status: "pass",
        message: "Job store not yet created (no jobs run).",
        details: { jobCount: 0 }
      };
    }
    return {
      name: "job_store",
      status: "warn",
      message: `Job store read issue: ${error instanceof Error ? error.message : String(error)}`
    };
  }
}

async function checkUnifiedDiscovery(flags: Record<string, string | boolean>, runtime: DoctorRuntime, requestId: string): Promise<DoctorCheck> {
  const builtinCount = builtinToolMetadata().length;
  const includeCustom = shouldDiscoverProjectTools(flags, runtime);
  const discovery = includeCustom ? await discoverProjectTools(flags, runtime, `${requestId}_project_tools`) : { ok: true as const, tools: [], mode: "skipped" as const };
  if (!discovery.ok) {
    return {
      name: "unified_tool_discovery",
      status: "warn",
      message: `Built-in tool metadata is available (${builtinCount}), but custom tool discovery failed: ${discovery.error?.code ?? "unknown"}.`,
      details: { builtinCount, projectTools: { status: "error", code: discovery.error?.code, message: discovery.error?.message } }
    };
  }

  return {
    name: "unified_tool_discovery",
    status: "pass",
    message: `Unified discovery is available: ${builtinCount} built-in tool(s), ${discovery.tools.length} custom tool(s).`,
    details: { builtinCount, projectToolCount: discovery.tools.length, projectDiscoveryMode: discovery.mode }
  };
}

async function checkContextDiscovery(flags: Record<string, string | boolean>, runtime: DoctorRuntime, requestId: string): Promise<DoctorCheck> {
  const includeCustom = shouldDiscoverContexts(flags, runtime);
  if (!includeCustom) {
    return {
      name: "context_discovery",
      status: "pass",
      message: "Context discovery skipped (no bridge or include-custom flag). Built-in contexts are always available offline.",
      details: { mode: "skipped", projectCustomCount: 0, builtInCount: 0 }
    };
  }

  const discovery = await discoverProjectContexts(flags, runtime, `${requestId}_context`, { requireBridge: false });
  if (!discovery.ok) {
    return {
      name: "context_discovery",
      status: "warn",
      message: `Context discovery failed: ${discovery.error?.code ?? "unknown"} — ${discovery.error?.message ?? "no message"}`,
      details: { mode: "error", projectCustomCount: 0, builtInCount: 0, errorCode: discovery.error?.code, errorMessage: discovery.error?.message }
    };
  }

  // Count categories from project custom contexts
  const projectContexts = discovery.contexts.filter((c) => c.source === "project");
  const categories = [...new Set(projectContexts.map((c) => c.category))];
  const conflictCount = countContextConflicts(discovery.contexts);
  const unavailableCount = projectContexts.filter((c) => c.supportStatus === "unavailable").length;

  const status: DoctorCheck["status"] = conflictCount > 0 || unavailableCount > 0 ? "warn" : "pass";
  return {
    name: "context_discovery",
    status,
    message: status === "pass"
      ? `Context discovery available: ${discovery.builtInCount} built-in, ${discovery.projectCustomCount} project custom (mode: ${discovery.mode}).`
      : `Context discovery available: ${discovery.builtInCount} built-in, ${discovery.projectCustomCount} project custom, but ${unavailableCount} unavailable, ${conflictCount} conflict(s) (mode: ${discovery.mode}).`,
    details: { mode: discovery.mode, builtInCount: discovery.builtInCount, projectCustomCount: discovery.projectCustomCount, categories, unavailableCount, conflictCount }
  };
}

function countContextConflicts(contexts: ContextDescriptor[]): number {
  // Count project contexts that were rejected due to name collision with built-in
  // (This is a structural check — in discoverProjectContexts, duplicates are already filtered,
  // so we count how many project contexts have names matching built-in names in the raw set)
  const names: Map<string, number> = new Map();
  for (const c of contexts) {
    names.set(c.name, (names.get(c.name) ?? 0) + 1);
  }
  let conflicts = 0;
  for (const [name, count] of names) {
    if (count > 1) conflicts++;
  }
  return conflicts;
}

function checkContextFingerprintStatus(checks: DoctorCheck[]): DoctorCheck | undefined {
  // Check bridge response for custom context fingerprint data
  const bridgeCheck = checks.find((check) => check.name === "bridge_connectivity" && check.status === "pass");
  const bridgeResponse = (bridgeCheck?.details as { bridgeResponse?: unknown } | undefined)?.bridgeResponse;
  if (!bridgeResponse || typeof bridgeResponse !== "object") {
    return undefined;
  }

  const customContexts = (bridgeResponse as { customContexts?: unknown }).customContexts;
  if (!customContexts || typeof customContexts !== "object") {
    return undefined;
  }

  const fingerprint = (customContexts as { fingerprint?: unknown }).fingerprint;
  if (!fingerprint || typeof fingerprint !== "object") {
    return undefined;
  }

  const blockedCount = numberProp(fingerprint, "blockedCount") ?? 0;
  const warnCount = numberProp(fingerprint, "warnCount") ?? 0;
  const contextCount = numberProp(fingerprint, "contextCount") ?? 0;
  const mode = stringProp(fingerprint, "mode") ?? "unknown";

  const status: DoctorCheck["status"] = blockedCount > 0 ? "fail" : warnCount > 0 ? "warn" : "pass";
  return {
    name: "project_context_fingerprint",
    status,
    message: status === "pass"
      ? `Custom context fingerprints match for ${contextCount} context(s) (mode: ${mode}).`
      : `Custom context fingerprint issues: ${blockedCount} blocked, ${warnCount} warning(s) (mode: ${mode}).`,
    details: { fingerprint }
  };
}

async function checkConfigIntegrity(healthyInstances: UnityInstance[]): Promise<DoctorCheck> {
  const issues: string[] = [];

  for (const instance of healthyInstances) {
    if (!instance.bridgeEndpoint) {
      issues.push(`Instance ${instance.instanceId}: missing bridgeEndpoint.`);
    }
    if (!instance.projectPath) {
      issues.push(`Instance ${instance.instanceId}: missing projectPath.`);
    }
    if (!instance.projectHash) {
      issues.push(`Instance ${instance.instanceId}: missing projectHash.`);
    }
    if (!instance.lastHeartbeatAt && !instance.lastSeenAt) {
      issues.push(`Instance ${instance.instanceId}: missing lastSeenAt.`);
    }
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
    message: healthyInstances.length > 0
      ? `${healthyInstances.length} instance(s) passed configuration integrity checks.`
      : "No instances to check.",
    details: { checkedInstances: healthyInstances.length }
  };
}

function buildSuggestions(checks: DoctorCheck[]): string[] {
  const suggestions: string[] = [];

  for (const check of checks) {
    if (check.status === "fail" || check.status === "warn") {
      switch (check.name) {
        case "instance_registry":
          if (check.status === "warn") {
            suggestions.push("Open a Unity project with the Unity CLI Bridge package installed.");
            suggestions.push("Run unity_cli init --project <path> to set up the bridge.");
          }
          break;
        case "healthy_instances":
          if (check.status === "fail") {
            suggestions.push("Reopen the Unity Editor for the target project.");
            suggestions.push("Run unity_cli instances prune to remove stale entries.");
          }
          break;
        case "bridge_connectivity":
          suggestions.push("Ensure Unity Editor is running with the Unity CLI Bridge package.");
          suggestions.push("Check --bridge-endpoint or UNITY_CLI_BRIDGE_ENDPOINT.");
          break;
        case "project_tool_fingerprint":
          suggestions.push("Inspect unity_cli doctor fingerprint details, then review project tool descriptor/handler code changes.");
          suggestions.push("Use tools run --fingerprint-mode warn only for an intentional, audited mismatch.");
          break;
        case "project_context_fingerprint":
          suggestions.push("Run unity_cli context list --include-custom to inspect custom context descriptors and fingerprints.");
          suggestions.push("Run unity_cli context describe <name> to review a specific custom context descriptor and fingerprint.");
          break;
        case "context_discovery":
          suggestions.push("Run unity_cli context list --include-custom to enumerate built-in and project custom contexts.");
          suggestions.push("Run unity_cli context describe <name> or context read <name> to inspect a specific custom context.");
          break;
        case "data_directory":
          suggestions.push("Run any unity_cli command to auto-create the data directory.");
          break;
        case "config_integrity":
          suggestions.push("Re-initialize the instance by restarting Unity Editor.");
          break;
        case "unified_tool_discovery":
          suggestions.push("Run unity_cli tools list --include-custom with a reachable Unity Bridge to inspect project custom descriptors.");
          break;
      }
    }
  }

  if (suggestions.length === 0) {
    suggestions.push("All checks passed. No action needed.");
  }

  return suggestions;
}
