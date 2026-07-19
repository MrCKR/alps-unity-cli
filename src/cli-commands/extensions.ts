/**
 * 扩展包命令：extensions list/install/remove/doctor。
 *
 * list 查询扩展可用性、安装状态和 Unity Package 映射；install 将扩展目录安装到
 * Packages/com.unity_cli/Extensions/<id>；remove 只移除扩展目录中由 manifest 管理的文件；
 * doctor 诊断依赖、物理文件和状态文件是否一致。
 *
 * 每个项目的启用状态保存在 <project>/Packages/com.unity_cli/extensions.json；
 * 物理文件清单保存在 <project>/Packages/com.unity_cli/unity_cli.extensions.manifest.json。
 * 核心 CLI 不在编译期依赖任何可选 Unity Package。
 */

import { readFile, writeFile, mkdir, rename, rm } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { join, resolve } from "node:path";
import { getString } from "../args.js";
import { failure, success } from "../output.js";
import { executeWithLocalRequestJournal } from "../local-request-journal.js";
import { ProjectIdentityError, resolveProjectIdentity } from "../project-identity.js";
import { defaultDataDir } from "../platform.js";
import { readUnityManifestDependencies, PackageManagerError } from "../package-manager.js";
import { StructuredToolError } from "../tools/errors.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import {
  ExtensionRegistry,
  ExtensionRegistryError,
  loadExtensionDescriptor,
  extensionAvailabilitySummary,
} from "../extensions/index.js";
import { readInstalledUnityPackages } from "../extensions/project-resolver.js";
import {
  checkExtensionBundles,
  installExtensionBundleUnchecked,
  removeExtensionBundle,
  readExtensionInstallManifest,
  resolveInstallAvailability,
  ExtensionInstallerError,
  type ExtensionStatusCheck
} from "../extensions/installer.js";
import type { ExtensionId } from "../extensions/types.js";

// ── Install state store ────────────────────────────────────────────────────

/** Location of the per-project extension install state file. */
function extensionsStatePath(projectPath: string): string {
  return join(resolve(projectPath), "Packages", "com.unity_cli", "extensions.json");
}

/** 读取启用状态；只有不存在的状态文件可视为空，损坏内容必须 fail closed。 */
export async function readInstalledExtensions(projectPath: string): Promise<Set<ExtensionId>> {
  const statePath = extensionsStatePath(projectPath);
  let content: string;
  try {
    content = await readFile(statePath, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return new Set();
    throw error;
  }
  try {
    const parsed = JSON.parse(content) as { extensions?: unknown };
    if (!Array.isArray(parsed.extensions) || !parsed.extensions.every((id) => typeof id === "string")) throw new Error("extensions must be a string array");
    return new Set(parsed.extensions as ExtensionId[]);
  } catch {
    throw new ExtensionStateError("E_EXTENSION_STATE_INVALID", `Extension state file is invalid: ${statePath}`, ["Repair or remove extensions.json only after inspecting its contents."]);
  }
}

/** 原子替换状态文件并从目标位置回读，避免成功响应依赖内存状态。 */
export async function writeInstalledExtensions(projectPath: string, ids: Set<ExtensionId>): Promise<void> {
  const statePath = extensionsStatePath(projectPath);
  const dir = resolve(statePath, "..");
  await mkdir(dir, { recursive: true });
  const temporaryPath = `${statePath}.${process.pid}.${randomUUID()}.tmp`;
  try {
    await writeFile(temporaryPath, JSON.stringify({ extensions: [...ids].sort() }, null, 2) + "\n", "utf8");
    await rename(temporaryPath, statePath);
    const readback = await readInstalledExtensions(projectPath);
    if (readback.size !== ids.size || [...ids].some((id) => !readback.has(id))) throw new ExtensionStateError("E_EXTENSION_STATE_INVALID", "Extension state readback did not match the committed state.");
  } finally {
    await rm(temporaryPath, { force: true }).catch(() => undefined);
  }
}

export class ExtensionStateError extends Error {
  public constructor(public readonly code: string, message: string, public readonly nextActions: string[] = []) { super(message); }
}

// ── Command handler ────────────────────────────────────────────────────────

export async function handleExtensionsCommand(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  try {
    if (!command || command === "list") return await handleExtensionsList(flags, requestId);
    if (command === "install") return await handleExtensionsInstall(args[0] ?? getString(flags, "name"), flags, runtime, requestId);
    if (command === "remove") return await handleExtensionsRemove(args[0] ?? getString(flags, "name"), flags, runtime, requestId);
    if (command === "doctor") return await handleExtensionsDoctor(flags, requestId);
    return failure(requestId, "UNKNOWN_EXTENSIONS_COMMAND", `Expected extensions list, install, remove or doctor (got "${command ?? ""}").`, ["Run unity_cli extensions list to see available extensions."]);
  } catch (error) {
    if (error instanceof ExtensionStateError || error instanceof ExtensionInstallerError || error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
    if (error instanceof ProjectIdentityError) return failure(requestId, error.code, error.message, ["Pass the root of an existing Unity project."], error.details);
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details,
      error.transient === undefined ? undefined : { transient: error.transient, retryAfterMs: error.retryAfterMs });
    throw error;
  }
}

// ── extensions list ────────────────────────────────────────────────────────

async function handleExtensionsList(
  flags: Record<string, string | boolean>,
  requestId: string
): Promise<CliResult> {
  const projectPath = resolveProject(flags);
  const registry = new ExtensionRegistry();
  const installedPackages = projectPath
    ? await readInstalledUnityPackages(projectPath)
    : undefined;
  const enabledExtensions = projectPath
    ? await readInstalledExtensions(projectPath)
    : new Set<ExtensionId>();
  const physicalChecks = projectPath
    ? await checkExtensionBundles(projectPath)
    : [];

  const descriptors = registry.extensionIds().map((id) =>
    loadExtensionDescriptor(registry, id, installedPackages)
  );

  const extensions = descriptors.map((desc) => {
    const availability = desc.availability;
    const physical = physicalChecks.find((check) => check.extensionId === desc.manifest.id);
    const providedTools = desc.tools.map((tool) => tool.name);
    return {
      id: desc.manifest.id,
      name: desc.manifest.name,
      description: desc.manifest.description,
      version: desc.manifest.version,
      enabled: enabledExtensions.has(desc.manifest.id),
      installed: physical?.installed ?? enabledExtensions.has(desc.manifest.id),
      status: physical?.status ?? (availability.status === "available" ? "available_not_installed" : "missing_dependency"),
      dependencyInstalled: availability.status === "available",
      availability: availability.status,
      unityPackages: desc.manifest.unityPackages.map((pkg) => ({
        packageId: pkg.packageId,
        displayName: pkg.displayName,
        installed: availability.packages.find((p) => p.packageId === pkg.packageId)?.installed ?? false,
        version: availability.packages.find((p) => p.packageId === pkg.packageId)?.version
      })),
      missingPackageIds: availability.missingPackageIds,
      providedTools,
      installCommand: `unity_cli extensions install ${desc.manifest.id} --project <path>`,
      nextActions: physical?.nextActions ?? availability.nextActions,
      toolCount: desc.tools.length,
      contextCount: desc.contexts.length
    };
  });

  const data: Record<string, unknown> = {
    command: "extensions.list",
    projectPath: projectPath ?? process.cwd(),
    projectResolved: projectPath !== undefined,
    count: extensions.length,
    extensions
  };

  const format = getString(flags, "format");
  if (format === "text") {
    const lines = extensions.map((e) => {
      const state = e.installed ? "installed" : e.status;
      const avail = e.dependencyInstalled ? "✓" : "✗";
      return `${e.id} [${state}] ${avail} ${e.name} — ${e.unityPackages.map((p) => p.packageId).join(", ")}`;
    });
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

// ── extensions install ────────────────────────────────────────────────────

async function handleExtensionsInstall(
  extensionId: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  if (!extensionId) {
    return failure(requestId, "EXTENSION_NAME_REQUIRED", "Pass an extension id: unity_cli extensions install <name>.", [
      "Run unity_cli extensions list to see available extension ids."
    ]);
  }

  const registry = new ExtensionRegistry();
  if (!registry.hasExtension(extensionId)) {
    return failure(requestId, "UNKNOWN_EXTENSION", `Unknown extension: ${extensionId}`, [
      "Run unity_cli extensions list to see available extension ids.",
      `Available extensions: ${registry.extensionIds().join(", ")}`
    ]);
  }

  const projectPath = resolveProject(flags);
  if (!projectPath) {
    return failure(requestId, "PROJECT_PATH_REQUIRED", "A project path is required to install an extension. Pass --project <path>.", [
      "Run unity_cli extensions install <name> --project <path>.",
      "Run unity_cli extensions list --project <path> to see current state."
    ]);
  }

  const manifest = registry.getExtension(extensionId)!.manifest;
  const identity = resolveProjectIdentity(projectPath);
  const absoluteDeadlineMs = Date.now() + Math.max(1, Number(getString(flags, "timeout-ms")) || 60_000);

  return executeWithLocalRequestJournal({
    identity,
    requestId,
    command: "extensions.install",
    toolName: "extensions.install",
    input: { extensionId, manifestVersion: manifest.version },
    projectPath,
    dataDir: runtime.dataDir ?? defaultDataDir(),
    absoluteDeadlineMs,
    executionClass: "atomic",
    completionDriverId: "extension-install-readback"
  }, async (commitEffect) => {
    // 资源锁后冻结所有会影响 effect 的项目状态，避免并发请求基于旧视图提交。
    try {
      await readUnityManifestDependencies(projectPath);
    } catch (error) {
      if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
      throw error;
    }
    const installedPackages = await readInstalledUnityPackages(projectPath);
    const availability = resolveInstallAvailability(manifest, installedPackages);
    if (availability.status !== "available") {
      return failure(requestId, "E_EXTENSION_DEPENDENCY_MISSING", `Extension "${extensionId}" requires Unity package(s) that are not installed: ${availability.missingPackageIds.join(", ")}.`, [
        "Install the required Unity package in Packages/manifest.json or Unity Package Manager.",
        "Run tools run package.resolve after changing packages.",
        `Then run unity_cli extensions install ${extensionId} --project <path>.`
      ]);
    }
    let installManifest;
    let enabledBefore;
    try {
      installManifest = await readExtensionInstallManifest(projectPath);
      enabledBefore = await readInstalledExtensions(projectPath);
    } catch (error) {
      if (error instanceof ExtensionInstallerError || error instanceof ExtensionStateError) return failure(requestId, error.code, error.message, error.nextActions);
      throw error;
    }
    const alreadyInstalled = installManifest.extensions.some((entry) => entry.extensionId === extensionId);
    await commitEffect();
    const installResult = await installExtensionBundleUnchecked(projectPath, manifest);
    const enabled = new Set(enabledBefore);
    enabled.add(extensionId);
    await writeInstalledExtensions(projectPath, enabled);
    const freshEnabled = await readInstalledExtensions(projectPath);
    if (!freshEnabled.has(extensionId)) throw new ExtensionStateError("E_EXTENSION_STATE_INVALID", "Installed extension is absent from fresh state readback.");
    return success(requestId, {
      command: "extensions.install", extensionId, name: manifest.name,
      status: alreadyInstalled ? "already_installed" : installResult.status, availability: availability.status, projectPath,
      targetRoot: installResult.targetRoot, fileCount: installResult.fileCount, sourceHash: installResult.sourceHash,
      unityPackages: manifest.unityPackages.map((pkg) => ({ packageId: pkg.packageId, displayName: pkg.displayName, installed: availability.packages.find((p) => p.packageId === pkg.packageId)?.installed ?? false })),
      nextActions: [`Extension "${extensionId}" Unity-side directory is installed under Packages/com.unity_cli/Extensions/${extensionId}.`, "Wait for Unity to recompile, then run tools group list/describe/run for provided tools."]
    });
  });
}

// ── extensions remove ─────────────────────────────────────────────────────

async function handleExtensionsRemove(
  extensionId: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  if (!extensionId) {
    return failure(requestId, "EXTENSION_NAME_REQUIRED", "Pass an extension id: unity_cli extensions remove <name>.", [
      "Run unity_cli extensions list to see installed extension ids."
    ]);
  }

  const projectPath = resolveProject(flags);
  if (!projectPath) {
    return failure(requestId, "PROJECT_PATH_REQUIRED", "A project path is required to remove an extension. Pass --project <path>.", [
      "Run unity_cli extensions remove <name> --project <path>."
    ]);
  }

  const identity = resolveProjectIdentity(projectPath);
  const absoluteDeadlineMs = Date.now() + Math.max(1, Number(getString(flags, "timeout-ms")) || 60_000);
  return executeWithLocalRequestJournal({
    identity, requestId, command: "extensions.remove", toolName: "extensions.remove",
    input: { extensionId }, projectPath, dataDir: runtime.dataDir ?? defaultDataDir(), absoluteDeadlineMs,
    executionClass: "atomic", completionDriverId: "extension-remove-readback"
  }, async (commitEffect) => {
    try {
      await readUnityManifestDependencies(projectPath);
    } catch (error) {
      if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
      throw error;
    }
    await readInstalledUnityPackages(projectPath);
    let installManifest;
    let enabledBefore;
    try {
      installManifest = await readExtensionInstallManifest(projectPath);
      enabledBefore = await readInstalledExtensions(projectPath);
    } catch (error) {
      if (error instanceof ExtensionInstallerError || error instanceof ExtensionStateError) return failure(requestId, error.code, error.message, error.nextActions);
      throw error;
    }
    const entry = installManifest.extensions.find((item) => item.extensionId === extensionId);
    if (!entry) return failure(requestId, "EXTENSION_NOT_INSTALLED", `Extension is not installed: ${extensionId}`, ["Run unity_cli extensions list --project <path> to inspect extension state."]);
    await commitEffect();
    const removeResult = await removeExtensionBundle(projectPath, extensionId);
    const enabled = new Set(enabledBefore);
    enabled.delete(extensionId);
    await writeInstalledExtensions(projectPath, enabled);
    const freshEnabled = await readInstalledExtensions(projectPath);
    const freshManifest = await readExtensionInstallManifest(projectPath);
    if (freshEnabled.has(extensionId) || freshManifest.extensions.some((item) => item.extensionId === extensionId)) throw new ExtensionStateError("E_EXTENSION_STATE_INVALID", "Removed extension remains in fresh state readback.");
    return success(requestId, { command: "extensions.remove", extensionId, status: removeResult.status, projectPath, targetRoot: removeResult.targetRoot, fileCount: removeResult.fileCount, nextActions: [`Extension "${extensionId}" bundle files listed in the manifest were removed.`, "The required Unity package was not removed."] });
  });
}

// ── extensions doctor ──────────────────────────────────────────────────────

async function handleExtensionsDoctor(
  flags: Record<string, string | boolean>,
  requestId: string
): Promise<CliResult> {
  const projectPath = resolveProject(flags);
  const registry = new ExtensionRegistry();

  if (!projectPath) {
    return failure(requestId, "PROJECT_PATH_REQUIRED", "A project path is required to diagnose extensions. Pass --project <path>.", [
      "Run unity_cli extensions doctor --project <path>.",
      "Run unity_cli extensions list to see available extensions."
    ]);
  }

  const installedPackages = await readInstalledUnityPackages(projectPath);
  const enabledExtensions = await readInstalledExtensions(projectPath);

  const summary = extensionAvailabilitySummary(registry, installedPackages);
  const physicalChecks = await checkExtensionBundles(projectPath);

  const checks = registry.extensionIds().map((id) => {
    const desc = loadExtensionDescriptor(registry, id, installedPackages);
    const availability = desc.availability;
    const physical = physicalChecks.find((check) => check.extensionId === id)!;
    const enabled = enabledExtensions.has(id) || physical.installed;

    const issues: string[] = [];
    if (availability.status === "available" && !physical.installed) {
      issues.push(`Extension "${id}" has all dependencies installed but its Unity-side directory is not installed.`);
    }
    if (physical.status === "missing_dependency" && enabled) {
      issues.push(`Extension "${id}" is installed but required Unity packages are missing: ${availability.missingPackageIds.join(", ")}`);
    }
    if (physical.status === "outdated") {
      issues.push(`Extension "${id}" managed files are missing, changed, or source hash is outdated.`);
    }
    if (physical.status === "unknown_files") {
      issues.push(`Extension "${id}" install directory contains unknown files: ${physical.unknownFiles.join(", ")}`);
    }
    if (availability.status === "unknown") {
      issues.push(`Extension "${id}" availability could not be resolved (no project packages read).`);
    }

    return {
      id,
      name: desc.manifest.name,
      enabled,
      installed: physical.installed,
      status: physical.status,
      dependencyInstalled: physical.dependencyInstalled,
      availability: availability.status,
      missingPackageIds: availability.missingPackageIds,
      missingFiles: physical.missingFiles,
      changedFiles: physical.changedFiles,
      unknownFiles: physical.unknownFiles,
      issues
    };
  });

  // Detect unknown enabled extensions (enabled ids not in registry)
  const unknownEnabled: string[] = [];
  for (const id of enabledExtensions) {
    if (!registry.hasExtension(id)) {
      unknownEnabled.push(id);
    }
  }
  if (unknownEnabled.length > 0) {
    checks.push(...unknownEnabled.map((id): typeof checks[number] => ({
      id,
      name: id,
      enabled: true,
      installed: false,
      status: "unknown_files" as const,
      dependencyInstalled: false,
      availability: "unknown" as const,
      missingPackageIds: [],
      missingFiles: [],
      changedFiles: [],
      unknownFiles: [],
      issues: [`Extension "${id}" is enabled in project state but is not registered in the CLI extension registry.`]
    })));
  }

  const allIssues = checks.flatMap((c) => c.issues);
  const healthy = allIssues.length === 0;

  const data: Record<string, unknown> = {
    command: "extensions.doctor",
    projectPath,
    healthy,
    summary: {
      total: summary.total,
      available: summary.available,
      unavailable: summary.unavailable,
      unknown: summary.unknown,
      enabled: checks.filter((check) => check.enabled).length,
      installed: checks.filter((check) => check.installed).length
    },
    checks,
    issues: allIssues,
    nextActions: healthy
      ? ["All extensions are in a consistent state."]
      : allIssues
  };

  const format = getString(flags, "format");
  if (format === "text") {
    const lines: string[] = [`extensions doctor — project: ${projectPath}`, `healthy: ${healthy}`];
    for (const check of checks) {
      const icon = check.issues.length === 0 ? "✓" : "✗";
      lines.push(`  ${icon} ${check.id} [${check.installed ? "installed" : check.status}] (${check.availability}) ${check.name}`);
      for (const issue of check.issues) {
        lines.push(`      - ${issue}`);
      }
    }
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

// ── Helpers ───────────────────────────────────────────────────────────────

function resolveProject(flags: Record<string, string | boolean>): string | undefined {
  const project = getString(flags, "project");
  return project ? resolve(project) : undefined;
}
