import { ExtensionRegistry } from "./extensions/index.js";
import { readExtensionPhysicalStates, type ExtensionStatusCheck } from "./extensions/installer.js";
import { readInstalledUnityPackages } from "./extensions/project-resolver.js";
import type { ExtensionAvailabilityResult, ExtensionId } from "./extensions/types.js";
import { findBuiltinExtensionToolContribution } from "./extensions/tool-manifest.js";
import type { ResolvedToolMetadata, ToolMetadata } from "./tools/metadata/types.js";
import { getString } from "./args.js";
import { currentInstanceProjectPath } from "./instances.js";

/** Extension capability 的统一可用性结论。dependencyInstalled 与 installed 永不互相替代。 */
export interface ExtensionAvailabilityCheck {
  available: boolean;
  extensionId?: ExtensionId;
  availabilityResult?: ExtensionAvailabilityResult;
  physicalState?: ExtensionStatusCheck;
  nextActions: string[];
  errorCode: string;
  message: string;
}

/**
 * 对 live extension Tool 执行 physical install gate。
 * TS-local Tool 不经过此 gate；extension Tool 即使没有 package dependency 也必须已安装。
 */
export async function checkToolExtensionAvailability(tool: ToolMetadata, flags: Record<string, string | boolean>): Promise<ExtensionAvailabilityCheck> {
  const registry = new ExtensionRegistry();
  const extensionId = findExtensionForTool(registry, tool.name);
  return extensionId ? checkExtensionPhysicalAvailability(registry, extensionId, getString(flags, "project"), `Tool \"${tool.name}\"`, tool.requiresPackages) : available();
}

/**
 * physical gate 优先使用显式 project，否则复用实例选择语义推断当前目标项目。
 * bridge endpoint 没有可用 registry 项目时保留原 flags，使调用方返回可恢复的 unknown。
 */
export async function extensionAvailabilityFlags(
  flags: Record<string, string | boolean>,
  runtime: { dataDir?: string }
): Promise<Record<string, string | boolean>> {
  if (getString(flags, "project") || !runtime.dataDir) return flags;
  const project = await currentInstanceProjectPath(flags, runtime.dataDir);
  return project ? { ...flags, project } : flags;
}

/** Extension 不提供离线 Tool descriptor；完整 descriptor/schema 只能由 live Bridge 返回。 */
export async function collectExtensionToolDescriptors(
  _flags: Record<string, string | boolean>
): Promise<Array<ResolvedToolMetadata & { extensionId?: ExtensionId; extensionAvailable?: boolean }>> {
  return [];
}

async function checkExtensionPhysicalAvailability(
  registry: ExtensionRegistry,
  extensionId: ExtensionId,
  projectPath: string | undefined,
  subject: string,
  capabilityPackages?: string[]
): Promise<ExtensionAvailabilityCheck> {
  const extension = registry.getExtension(extensionId);
  if (!extension) return available();
  if (!projectPath) {
    return unavailable(extensionId, undefined, undefined, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN", `${subject} requires a project path to verify extension physical installation.`, [
      "Select the running Unity project with --instance <id|canonical-project-path|alias>.",
      `Run unitycli extensions doctor --project <path> to inspect ${extensionId}.`
    ]);
  }

  const physicalStates = await readExtensionPhysicalStates(projectPath);
  const physicalState = physicalStates.get(extensionId);
  if (!physicalState) {
    return unavailable(extensionId, undefined, undefined, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN", `${subject} has no physical state record for extension \"${extensionId}\".`, [
      `Run unitycli extensions doctor --project ${projectPath}.`
    ]);
  }
  const installedPackages = await readInstalledUnityPackages(projectPath);
  const requiredPackageIds = capabilityPackages ?? extension.manifest.unityPackages.map((pkg) => pkg.packageId);
  const missingPackageIds = requiredPackageIds.filter((packageId) => !installedPackages.has(packageId));
  const availability = registry.resolveAvailability(extensionId, installedPackages);
  const dependencyInstalled = physicalState.dependencyInstalled && missingPackageIds.length === 0;
  if (physicalState.installed && dependencyInstalled) {
    return { available: true, extensionId, availabilityResult: availability, physicalState, nextActions: [], errorCode: "", message: "" };
  }
  const code = dependencyInstalled ? "E_EXTENSION_NOT_INSTALLED" : "E_EXTENSION_DEPENDENCY_MISSING";
  const unsatisfiedPackageIds = missingPackageIds.length > 0
    ? missingPackageIds
    : availability.missingPackageIds.length > 0
      ? availability.missingPackageIds
      : physicalState.missingPackageIds;
  const reason = dependencyInstalled
    ? `extension files are ${physicalState.status}`
    : `required Unity package profile(s) are unavailable: ${unsatisfiedPackageIds.map((packageId) => {
        const requirement = extension.manifest.unityPackages.find((pkg) => pkg.packageId === packageId);
        const expected = requirement?.versionRange ? `${packageId}@${requirement.versionRange}` : packageId;
        const actual = installedPackages.get(packageId);
        return actual === undefined ? `${expected} (not installed)` : `${expected} (actual ${actual})`;
      }).join(", ")}`;
  return unavailable(extensionId, availability, physicalState, code, `${subject} is unavailable because ${reason}.`, physicalState.nextActions);
}

function available(): ExtensionAvailabilityCheck {
  return { available: true, nextActions: [], errorCode: "", message: "" };
}

function unavailable(
  extensionId: ExtensionId,
  availabilityResult: ExtensionAvailabilityResult | undefined,
  physicalState: ExtensionStatusCheck | undefined,
  errorCode: string,
  message: string,
  nextActions: string[]
): ExtensionAvailabilityCheck {
  return { available: false, extensionId, availabilityResult, physicalState, errorCode, message, nextActions };
}

function findExtensionForTool(registry: ExtensionRegistry, toolName: string): ExtensionId | undefined {
  const contribution = findBuiltinExtensionToolContribution(toolName);
  if (contribution) return contribution.extensionId;
  return registry.extensionIds().find((id) => registry.getExtension(id)!.manifest.contributions.tools?.some((tool) => tool.name === toolName));
}
