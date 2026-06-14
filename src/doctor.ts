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
import { readInstances, type UnityInstance } from "./instances.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge-commands.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export interface DoctorRuntime extends BridgeRuntime {
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
    healthyInstances = instances.filter((i) => i.status === "healthy");
    checks.push(checkHealthyInstances(instances, healthyInstances));
  } else {
    checks.push({ name: "healthy_instances", status: "warn", message: "Skipped: instance registry not available." });
  }

  // Check 3: Bridge connectivity
  const endpoint = getString(flags, "bridge-endpoint") ?? runtime.bridgeEndpoint ?? process.env.ALPS_UNITY_BRIDGE_ENDPOINT;
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
    message: `${healthy.length} healthy instance(s) found.`,
    details: {
      healthyCount: healthy.length,
      staleCount: stale.length,
      instances: healthy.map((i) => ({
        instanceId: i.instanceId,
        projectPath: i.projectPath,
        bridgeEndpoint: i.bridgeEndpoint,
        lastHeartbeatAt: i.lastHeartbeatAt
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
    if (!instance.lastHeartbeatAt) {
      issues.push(`Instance ${instance.instanceId}: missing lastHeartbeatAt.`);
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
            suggestions.push("Open a Unity project with the Alps Unity Bridge package installed.");
            suggestions.push("Run unity_cli init --project <path> to set up the bridge.");
          }
          break;
        case "healthy_instances":
          if (check.status === "fail") {
            suggestions.push("Reopen the Unity Editor for the target project.");
            suggestions.push("Run unity_cli instances cleanup to remove stale entries.");
          }
          break;
        case "bridge_connectivity":
          suggestions.push("Ensure Unity Editor is running with the Alps Unity Bridge package.");
          suggestions.push("Check --bridge-endpoint or ALPS_UNITY_BRIDGE_ENDPOINT.");
          break;
        case "data_directory":
          suggestions.push("Run any unity_cli command to auto-create the data directory.");
          break;
        case "config_integrity":
          suggestions.push("Re-initialize the instance by restarting Unity Editor.");
          break;
      }
    }
  }

  if (suggestions.length === 0) {
    suggestions.push("All checks passed. No action needed.");
  }

  return suggestions;
}
