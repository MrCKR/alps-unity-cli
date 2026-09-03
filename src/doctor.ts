/**
 * CLI control command: doctor.
 *
 * Registry/Health identity, main-thread pulse, admitted business work, catalog
 * integrity, compilation, and extension installation are independent owners.
 */

import { getString } from "./args.js";
import { Buffer } from "node:buffer";
import { access, stat } from "node:fs/promises";
import {
  inspectInstanceHealth,
  instanceSelectionFailure,
  obsoleteLocalStatePaths,
  readInstancesWithRegistryDiagnostics,
  selectInstanceForFlags,
  type InstanceHealthInspection,
  type InstanceSelection,
} from "./instances.js";
import {
  executeBridgePayload,
  SessionSupervisor,
  SessionSupervisorError,
  type BridgeRuntime,
  type RootAdmissionLease,
} from "./bridge/index.js";
import { EDITOR_STATE_OBSERVATION_BUDGET_MS } from "./bridge/session-supervisor.js";
import { withObservationSelection } from "./bridge/types.js";
import { extensionAvailabilityFlags } from "./extension-availability.js";
import { readInstalledExtensions } from "./cli-commands/extensions.js";
import { checkExtensionBundles, readExtensionInstallManifest, type ExtensionStatusCheck } from "./extensions/installer.js";
import { EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT } from "./extensions/state-contract.js";
import { failure, success } from "./output.js";
import type { CliError, CliResult } from "./types.js";

const DOCTOR_BUSINESS_TIMEOUT_MS = 10_000;
const DOCTOR_BUSINESS_COMPLETION = "doctor-business-read-v1";
const DOCTOR_CATALOG_IDENTITY = "unity-cli-immutable-domain-catalog";
const MAX_DOCTOR_CATALOG_TOOLS = 10_000;
const MAX_DOCTOR_CATALOG_ASSEMBLIES = 100_000;
const MAX_DOCTOR_CATALOG_RESPONSE_BYTES = 64 * 1024;

interface DoctorCatalogProviderCounts {
  builtIn: number;
  extension: number;
  projectInterface: number;
  officialAttribute: number;
}

interface DoctorCatalogPublicationDiagnostics {
  assemblyCount: number;
  providerAssemblyCount: number;
  elapsedMilliseconds: number;
  discoveryPassCount: number;
  assetDatabaseOperationCount: number;
  sourceFileReadCount: number;
  persistedToolValueOperationCount: number;
  perToolSuccessLogCount: number;
}

interface DoctorCatalogData {
  identity: typeof DOCTOR_CATALOG_IDENTITY;
  toolCount: number;
  providerCounts: DoctorCatalogProviderCounts;
  publicationDiagnostics: DoctorCatalogPublicationDiagnostics;
}

export interface DoctorRuntime extends BridgeRuntime {
  dataDir: string;
  selectInstance?: typeof selectInstanceForFlags;
}

export interface DoctorCheck {
  name: string;
  status: "pass" | "fail" | "warn";
  message: string;
  details?: Record<string, unknown>;
}

type SelectedHealth = {
  selection?: InstanceSelection;
  health?: InstanceHealthInspection;
  error?: CliError;
};

type PrivateDoctorRead = { data: unknown } | DoctorCheck;

export async function handleDoctorCommand(
  flags: Record<string, string | boolean>,
  runtime: DoctorRuntime,
  requestId: string
): Promise<CliResult> {
  const checks: DoctorCheck[] = [];
  checks.push(await checkDataDirectory(runtime.dataDir));
  checks.push(await checkObsoleteLocalState(runtime.dataDir));

  const selected = await inspectSelectedHealth(flags, runtime, requestId);
  checks.push(await checkRegistryHealthIdentity(runtime.dataDir, selected));
  checks.push(checkMainThreadPulse(selected));
  checks.push(await checkBusinessDiagnostic(flags, runtime, requestId, selected));
  checks.push(await checkCatalogIntegrity(flags, runtime, requestId, selected));
  checks.push(await checkCompile(flags, runtime, requestId, selected));
  checks.push(await checkExtensions(flags, runtime));

  const summary = checks.some((check) => check.status === "fail")
    ? "fail"
    : checks.some((check) => check.status === "warn") ? "warn" : "pass";
  return success(requestId, { summary, checks, suggestions: buildSuggestions(checks) });
}

/** selector 与严格 Health v3 只执行一次，后续 check 共享脱敏结果但不共享严重性。 */
async function inspectSelectedHealth(
  flags: Record<string, string | boolean>,
  runtime: DoctorRuntime,
  requestId: string,
): Promise<SelectedHealth> {
  try {
    const selection = await (runtime.selectInstance ?? selectInstanceForFlags)(flags, runtime.dataDir);
    return { selection, health: await inspectInstanceHealth(selection.instance) };
  } catch (error) {
    const projected = projectDoctorError(`${requestId}_identity`, error);
    return { error: projected };
  }
}

async function checkRegistryHealthIdentity(dataDir: string, selected: SelectedHealth): Promise<DoctorCheck> {
  let instanceCount = 0;
  let invalidRecordCount = 0;
  try {
    const registry = await readInstancesWithRegistryDiagnostics(dataDir);
    instanceCount = registry.instances.length;
    invalidRecordCount = registry.invalidRecordCount;
  } catch (error) {
    return {
      name: "registry_health_identity",
      status: "fail",
      message: `Instance registry could not be read: ${errorMessage(error)}`,
      details: { code: "E_INSTANCE_REGISTRY_INVALID" },
    };
  }

  if (!selected.selection) {
    const error = selected.error;
    const status: DoctorCheck["status"] = error?.code === "E_INSTANCE_CONFLICT" || error?.code === "E_INSTANCE_UNVERIFIABLE" ? "fail" : "warn";
    return {
      name: "registry_health_identity",
      status,
      message: error?.message ?? "No exact Editor identity was selected from Registry v3.",
      details: {
        code: error?.code ?? "E_INSTANCE_REQUIRED",
        instanceCount,
        invalidRecordCount,
        transportVerified: false,
      },
    };
  }

  const identity = selected.selection.instance;
  if (!selected.health?.transportVerified) {
    return {
      name: "registry_health_identity",
      status: "fail",
      message: `Registry v3 selected ${identity.instanceId}, but strict Health v3 transport identity was not verified.`,
      details: {
        code: "BRIDGE_CONNECTION_FAILED",
        instanceId: identity.instanceId,
        canonicalProjectPath: identity.canonicalProjectPath,
        registryStatus: identity.status,
        instanceCount,
        invalidRecordCount,
        transportVerified: false,
      },
    };
  }

  return {
    name: "registry_health_identity",
    status: invalidRecordCount > 0 ? "warn" : "pass",
    message: invalidRecordCount > 0
      ? `Strict Registry/Health identity is verified; ${invalidRecordCount} unrelated invalid registry record(s) were ignored.`
      : "Strict Registry v3 and Health v3 transport identity are verified.",
    details: {
      instanceId: identity.instanceId,
      canonicalProjectPath: identity.canonicalProjectPath,
      editorSessionId: identity.editorSessionId,
      registryStatus: identity.status,
      instanceCount,
      invalidRecordCount,
      transportVerified: true,
    },
  };
}

function checkMainThreadPulse(selected: SelectedHealth): DoctorCheck {
  const pulse = selected.health?.mainThread;
  if (!selected.health?.transportVerified || !pulse) {
    return {
      name: "main_thread_pulse",
      status: "warn",
      message: "Main-thread pulse could not be evaluated independently of transport identity.",
      details: { code: "E_MAIN_THREAD_PULSE_UNAVAILABLE", observed: false, fresh: false },
    };
  }
  if (!pulse.fresh) {
    return {
      name: "main_thread_pulse",
      status: "warn",
      message: `Health v3 transport is reachable, but the Editor main-thread pulse is stale or absent (${pulse.lifecycle}).`,
      details: { code: "E_MAIN_THREAD_UNRESPONSIVE", ...pulse },
    };
  }
  return {
    name: "main_thread_pulse",
    status: pulse.lifecycle === "ready" ? "pass" : "warn",
    message: pulse.lifecycle === "ready"
      ? "The read-only Editor main-thread pulse is fresh."
      : `The main-thread pulse is fresh while the Editor lifecycle is ${pulse.lifecycle}.`,
    details: { ...pulse },
  };
}

/** 此 check 独占 admission；其失败不得改写 Registry/Health check。 */
async function checkBusinessDiagnostic(
  flags: Record<string, string | boolean>,
  runtime: DoctorRuntime,
  requestId: string,
  selected: SelectedHealth,
): Promise<DoctorCheck> {
  if (!selected.selection || !selected.health?.transportVerified || !selected.health.mainThread?.fresh) {
    return skippedLiveCheck("business_diagnostic", "E_BUSINESS_DIAGNOSTIC_SKIPPED", "Business diagnostic was skipped because an exact fresh Editor session was not available.");
  }

  const childRequestId = `${requestId}_doctor_business`;
  const input: Record<string, unknown> = {};
  const selectedFlags = { ...flags, instance: selected.selection.instance.instanceId };
  const supervisor = new SessionSupervisor(selectedFlags, runtime);
  let admission: RootAdmissionLease | undefined;
  try {
    admission = await supervisor.acquireRootAdmission({
      requestId: childRequestId,
      command: "internal.doctor.business",
      input,
      dryRun: false,
      target: "editor",
      completionContractIdentity: DOCTOR_BUSINESS_COMPLETION,
      timeoutMs: DOCTOR_BUSINESS_TIMEOUT_MS,
      admissionAbsoluteDeadlineMs: Date.now() + DOCTOR_BUSINESS_TIMEOUT_MS,
    });
    const result = await executeBridgePayload(
      childRequestId,
      "internal.doctor.business",
      input,
      selectedFlags,
      { ...runtime, rootAdmission: admission },
      { mode: "read", mutation: false, absoluteDeadlineMs: admission.executionAbsoluteDeadlineMs },
    );
    if (!result.ok) return failedLiveCheck("business_diagnostic", result.error);
    const data = record(result.data);
    if (data?.accepted !== true || typeof data.sceneLoaded !== "boolean" || typeof data.projectPath !== "string") {
      return invalidLiveCheck("business_diagnostic", "Business diagnostic returned an invalid DTO.");
    }
    return {
      name: "business_diagnostic",
      status: "pass",
      message: "An admission-bound read reached the Editor business layer.",
      details: {
        accepted: true,
        projectPath: data.projectPath,
        sceneLoaded: data.sceneLoaded,
        activeScenePath: typeof data.activeScenePath === "string" ? data.activeScenePath : "",
        playMode: data.playMode === true,
      },
    };
  } catch (error) {
    return failedLiveCheck("business_diagnostic", projectDoctorError(childRequestId, error));
  } finally {
    if (admission) await supervisor.completeRootAdmission(admission);
  }
}

async function checkCatalogIntegrity(
  flags: Record<string, string | boolean>,
  runtime: DoctorRuntime,
  requestId: string,
  selected: SelectedHealth,
): Promise<DoctorCheck> {
  const result = await privateDoctorRead("catalog_integrity", "internal.doctor.catalog", flags, runtime, requestId, selected);
  if (isDoctorCheck(result)) {
    return result.details?.code === "E_TOOL_REGISTRY_UNHEALTHY" ? { ...result, status: "fail" } : result;
  }
  const data = doctorCatalogData(result.data);
  if (!data) return invalidLiveCheck("catalog_integrity", "Catalog integrity read returned an invalid bounded DTO.");
  return {
    name: "catalog_integrity",
    status: "pass",
    message: `The immutable domain catalog contains ${data.toolCount} registered Tool(s).`,
    details: {
      identity: data.identity,
      toolCount: data.toolCount,
      providerCounts: data.providerCounts,
      publicationDiagnostics: data.publicationDiagnostics,
    },
  };
}

async function checkCompile(
  flags: Record<string, string | boolean>,
  runtime: DoctorRuntime,
  requestId: string,
  selected: SelectedHealth,
): Promise<DoctorCheck> {
  const result = await privateDoctorRead("compile", "internal.doctor.compile", flags, runtime, requestId, selected);
  if (isDoctorCheck(result)) return result;
  const data = record(result.data);
  const errors = Array.isArray(data?.compileErrors) && data.compileErrors.every((value) => typeof value === "string")
    ? data.compileErrors as string[] : undefined;
  const nextActions = Array.isArray(data?.nextActions) && data.nextActions.every((value) => typeof value === "string")
    ? data.nextActions as string[] : undefined;
  if (!data || typeof data.compiling !== "boolean" || typeof data.scanComplete !== "boolean"
    || typeof data.hasCompileErrors !== "boolean" || !isNonNegativeInteger(data.compileErrorCount)
    || typeof data.compileErrorsTruncated !== "boolean" || !errors || errors.length > 20 || !nextActions) {
    return invalidLiveCheck("compile", "Compile diagnostic returned an invalid bounded DTO.");
  }

  const status: DoctorCheck["status"] = data.hasCompileErrors ? "fail" : data.compiling || !data.scanComplete ? "warn" : "pass";
  const code = data.hasCompileErrors ? "E_UNITY_COMPILE_FAILED"
    : data.compiling ? "E_UNITY_COMPILING"
      : !data.scanComplete ? "E_COMPILE_DIAGNOSTICS_INCOMPLETE" : undefined;
  return {
    name: "compile",
    status,
    message: data.hasCompileErrors
      ? `Unity reports ${data.compileErrorCount} compiler error(s).`
      : data.compiling ? "Unity is compiling scripts."
        : !data.scanComplete ? "Compile diagnostics were not scanned completely."
          : "Unity scripts are not compiling and no compiler errors were found.",
    details: {
      ...(code ? { code } : {}),
      compiling: data.compiling,
      scanComplete: data.scanComplete,
      hasCompileErrors: data.hasCompileErrors,
      compileErrorCount: data.compileErrorCount,
      compileErrorsTruncated: data.compileErrorsTruncated,
      compileErrors: errors,
      nextActions,
    },
  };
}

async function privateDoctorRead(
  checkName: string,
  command: string,
  flags: Record<string, string | boolean>,
  runtime: DoctorRuntime,
  requestId: string,
  selected: SelectedHealth,
): Promise<PrivateDoctorRead> {
  if (!selected.selection || !selected.health?.transportVerified || !selected.health.mainThread?.fresh) {
    return skippedLiveCheck(checkName, "E_DOCTOR_READ_SKIPPED", `${checkName} was skipped because an exact fresh Editor session was not available.`);
  }
  const result = await executeBridgePayload(
    `${requestId}_${checkName}`,
    command,
    {},
    { ...flags, instance: selected.selection.instance.instanceId },
    runtime,
    withObservationSelection({ mode: "observeNow", mutation: false, defaultTimeoutMs: EDITOR_STATE_OBSERVATION_BUDGET_MS }, selected.selection),
  );
  return result.ok ? { data: result.data } : failedLiveCheck(checkName, result.error);
}

async function checkExtensions(flags: Record<string, string | boolean>, runtime: DoctorRuntime): Promise<DoctorCheck> {
  const resolvedFlags = await extensionAvailabilityFlags(flags, runtime);
  const projectPath = getString(resolvedFlags, "project");
  if (!projectPath) {
    return {
      name: "extensions",
      status: "warn",
      message: "Extension installation state is unknown because no project path was resolved.",
      details: { code: "PROJECT_PATH_REQUIRED", projectResolved: false },
    };
  }

  try {
    const [enabled, manifest, physical] = await Promise.all([
      readInstalledExtensions(projectPath),
      readExtensionInstallManifest(projectPath),
      checkExtensionBundles(projectPath),
    ]);
    const installedEntries = new Set(manifest.extensions.map((entry) => entry.extensionId));
    const knownIds = new Set(physical.map((entry) => entry.extensionId));
    const unknownInstalled = manifest.extensions.map((entry) => entry.extensionId).filter((id) => !knownIds.has(id));
    const unknownEnabled = [...enabled].filter((id) => !knownIds.has(id));
    const broken = physical.filter((entry) => (enabled.has(entry.extensionId) || installedEntries.has(entry.extensionId)) && !entry.installed);
    const optional = physical.filter((entry) => !enabled.has(entry.extensionId) && !installedEntries.has(entry.extensionId));
    const healthyInstalled = physical.filter((entry) => entry.installed).length;

    if (unknownInstalled.length > 0 || unknownEnabled.length > 0 || broken.length > 0) {
      const missingDependency = broken.some((entry) => entry.status === "missing_dependency");
      const code = unknownInstalled.length > 0 ? "E_EXTENSION_UNKNOWN_INSTALLED"
        : unknownEnabled.length > 0 ? "E_EXTENSION_STATE_INVALID"
          : missingDependency ? "E_EXTENSION_DEPENDENCY_MISSING" : "E_EXTENSION_TARGET_PROJECTION_INVALID";
      const nextActions = unknownInstalled.length > 0
        ? ["Inspect unitycli.extensions.manifest.json and the matching extension directories.", "Remove unknown installed entries only after auditing their projected files."]
        : unknownEnabled.length > 0
          ? ["Inspect extensions.json and remove IDs that are not present in the CLI extension registry."]
          : [...new Set(broken.flatMap((entry) => entry.nextActions))];
      return {
        name: "extensions",
        status: "fail",
        message: `${broken.length + unknownInstalled.length + unknownEnabled.length} installed or enabled extension state(s) are broken.`,
        details: {
          code,
          projectPath,
          healthyInstalled,
          optionalUninstalled: optional.length,
          unknownInstalledCount: unknownInstalled.length,
          unknownInstalled: unknownInstalled.slice(0, EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT),
          unknownInstalledTruncated: unknownInstalled.length > EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT,
          unknownEnabledCount: unknownEnabled.length,
          unknownEnabled: unknownEnabled.slice(0, EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT),
          unknownEnabledTruncated: unknownEnabled.length > EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT,
          brokenCount: broken.length,
          broken: broken.slice(0, EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT).map(extensionDiagnostic),
          brokenTruncated: broken.length > EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT,
          nextActions,
        },
      };
    }

    if (optional.length > 0) {
      return {
        name: "extensions",
        status: "warn",
        message: `${optional.length} optional extension(s) are not installed; no installed or enabled extension is broken.`,
        details: {
          projectPath,
          healthyInstalled,
          optionalUninstalled: optional.length,
          optional: optional.slice(0, EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT).map((entry) => ({
            extensionId: entry.extensionId,
            status: entry.status,
            missingPackageIds: entry.missingPackageIds,
          })),
          optionalTruncated: optional.length > EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT,
        },
      };
    }

    return {
      name: "extensions",
      status: "pass",
      message: `All ${healthyInstalled} installed extension(s) passed physical and dependency checks.`,
      details: { projectPath, healthyInstalled, optionalUninstalled: 0 },
    };
  } catch (error) {
    const projected = projectDoctorError("doctor_extensions", error);
    return {
      name: "extensions",
      status: "fail",
      message: projected.message,
      details: { code: projected.code, projectPath, nextActions: projected.nextActions },
    };
  }
}

function extensionDiagnostic(entry: ExtensionStatusCheck): Record<string, unknown> {
  return {
    extensionId: entry.extensionId,
    status: entry.status,
    dependencyInstalled: entry.dependencyInstalled,
    missingPackageIds: entry.missingPackageIds,
    missingFileCount: entry.missingFiles.length,
    changedFileCount: entry.changedFiles.length,
    unknownFileCount: entry.unknownFiles.length,
    nextActions: entry.nextActions,
  };
}

async function checkDataDirectory(dataDir: string): Promise<DoctorCheck> {
  try {
    await access(dataDir);
    if (!(await stat(dataDir)).isDirectory()) {
      return { name: "data_directory", status: "fail", message: `Data path is not a directory: ${dataDir}`, details: { code: "E_DATA_DIRECTORY_INVALID" } };
    }
    return { name: "data_directory", status: "pass", message: `Data directory exists: ${dataDir}`, details: { dataDir } };
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") {
      return { name: "data_directory", status: "warn", message: `Data directory does not exist yet: ${dataDir}.`, details: { dataDir } };
    }
    return { name: "data_directory", status: "fail", message: `Cannot access data directory: ${errorMessage(error)}`, details: { code: "E_DATA_DIRECTORY_INVALID" } };
  }
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

function projectDoctorError(requestId: string, error: unknown): CliError {
  const selected = instanceSelectionFailure(requestId, error);
  if (selected && !selected.ok) return selected.error;
  if (error instanceof SessionSupervisorError) {
    const projected = failure(requestId, error.code, error.message, error.nextActions, undefined, {
      transient: error.transient,
      retryAfterMs: error.retryAfterMs,
    });
    if (!projected.ok) return projected.error;
  }
  const code = typeof error === "object" && error !== null && "code" in error && typeof (error as { code?: unknown }).code === "string"
    ? (error as { code: string }).code : "BRIDGE_CONNECTION_FAILED";
  const nextActions = typeof error === "object" && error !== null && "nextActions" in error && Array.isArray((error as { nextActions?: unknown }).nextActions)
    ? (error as { nextActions: string[] }).nextActions : ["Run unitycli instances list and inspect the selected Editor session."];
  const projected = failure(requestId, code, errorMessage(error), nextActions);
  if (!projected.ok) return projected.error;
  throw new Error("Doctor error projection unexpectedly succeeded.");
}

function failedLiveCheck(name: string, error: CliError): DoctorCheck {
  return {
    name,
    status: error.transient ? "warn" : "fail",
    message: `${error.code}: ${error.message}`,
    details: {
      code: error.code,
      transient: error.transient,
      retryAfterMs: error.retryAfterMs,
      nextActions: error.nextActions,
    },
  };
}

function skippedLiveCheck(name: string, code: string, message: string): DoctorCheck {
  return { name, status: "warn", message, details: { code } };
}

function invalidLiveCheck(name: string, message: string): DoctorCheck {
  return { name, status: "fail", message, details: { code: "E_BRIDGE_RESPONSE_INVALID" } };
}

function isDoctorCheck(value: PrivateDoctorRead): value is DoctorCheck {
  return "status" in value && "name" in value;
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

/** catalog DTO 只接受唯一 published snapshot 的 aggregate，并校验 count parity 与固定 publication 边界。 */
function doctorCatalogData(value: unknown): DoctorCatalogData | undefined {
  const data = exactRecord(value, ["identity", "toolCount", "providerCounts", "publicationDiagnostics"]);
  if (!data) return undefined;
  const providerCounts = exactRecord(data.providerCounts, ["builtIn", "extension", "projectInterface", "officialAttribute"]);
  const publication = exactRecord(data.publicationDiagnostics, [
    "assemblyCount", "providerAssemblyCount", "elapsedMilliseconds", "discoveryPassCount",
    "assetDatabaseOperationCount", "sourceFileReadCount", "persistedToolValueOperationCount", "perToolSuccessLogCount",
  ]);
  const serialized = JSON.stringify(data);
  const toolCount = data.toolCount;
  if (!providerCounts || !publication || serialized === undefined
    || Buffer.byteLength(serialized, "utf8") > MAX_DOCTOR_CATALOG_RESPONSE_BYTES
    || data.identity !== DOCTOR_CATALOG_IDENTITY
    || !isNonNegativeInteger(toolCount) || toolCount > MAX_DOCTOR_CATALOG_TOOLS) return undefined;

  if (!isNonNegativeInteger(providerCounts.builtIn) || !isNonNegativeInteger(providerCounts.extension)
    || !isNonNegativeInteger(providerCounts.projectInterface) || !isNonNegativeInteger(providerCounts.officialAttribute)) return undefined;
  const typedProviderCounts: DoctorCatalogProviderCounts = {
    builtIn: providerCounts.builtIn,
    extension: providerCounts.extension,
    projectInterface: providerCounts.projectInterface,
    officialAttribute: providerCounts.officialAttribute,
  };
  if (Object.values(typedProviderCounts).some((count) => count > toolCount)
    || Object.values(typedProviderCounts).reduce((sum, count) => sum + count, 0) !== toolCount
    || !isNonNegativeInteger(publication.assemblyCount) || publication.assemblyCount > MAX_DOCTOR_CATALOG_ASSEMBLIES
    || !isNonNegativeInteger(publication.providerAssemblyCount) || publication.providerAssemblyCount > publication.assemblyCount
    || !isNonNegativeInteger(publication.elapsedMilliseconds)
    || publication.discoveryPassCount !== 1
    || publication.assetDatabaseOperationCount !== 0
    || publication.sourceFileReadCount !== 0
    || publication.persistedToolValueOperationCount !== 0
    || publication.perToolSuccessLogCount !== 0) return undefined;
  const typedPublication: DoctorCatalogPublicationDiagnostics = {
    assemblyCount: publication.assemblyCount,
    providerAssemblyCount: publication.providerAssemblyCount,
    elapsedMilliseconds: publication.elapsedMilliseconds,
    discoveryPassCount: publication.discoveryPassCount,
    assetDatabaseOperationCount: publication.assetDatabaseOperationCount,
    sourceFileReadCount: publication.sourceFileReadCount,
    persistedToolValueOperationCount: publication.persistedToolValueOperationCount,
    perToolSuccessLogCount: publication.perToolSuccessLogCount,
  };

  return {
    identity: DOCTOR_CATALOG_IDENTITY,
    toolCount,
    providerCounts: typedProviderCounts,
    publicationDiagnostics: typedPublication,
  };
}

function exactRecord(value: unknown, fields: readonly string[]): Record<string, unknown> | undefined {
  const item = record(value);
  if (!item) return undefined;
  const allowed = new Set(fields);
  const keys = Object.keys(item);
  return keys.length === fields.length && keys.every((key) => allowed.has(key)) ? item : undefined;
}

function record(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function buildSuggestions(checks: DoctorCheck[]): string[] {
  const suggestions: string[] = [];
  for (const check of checks) {
    if (check.status === "pass") continue;
    switch (check.name) {
      case "registry_health_identity":
        suggestions.push("Run unitycli instances list and select the exact canonical project identity.");
        break;
      case "main_thread_pulse":
        suggestions.push("Run unitycli tools run editor.state and wait for a fresh ready Editor pulse.");
        break;
      case "business_diagnostic":
        suggestions.push("Use the business diagnostic error code and nextActions without treating it as a transport failure.");
        break;
      case "catalog_integrity":
        suggestions.push("Fix the catalog publication failure, then let Unity compile and reload the domain.");
        break;
      case "compile":
        suggestions.push("Fix the bounded compiler diagnostics or wait for compilation to finish.");
        break;
      case "extensions":
        suggestions.push("Run unitycli extensions doctor --project <path> for installed extension details.");
        break;
      case "obsolete_local_state":
        suggestions.push("Delete only the explicitly reported obsolete files after auditing them.");
        break;
    }
  }
  return suggestions.length > 0 ? [...new Set(suggestions)] : ["All checks passed. No action needed."];
}
