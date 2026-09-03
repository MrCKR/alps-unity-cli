import { getString } from "./args.js";
import { ExtensionRegistry } from "./extensions/index.js";
import {
  checkSelectedExtensionBundle,
  readExtensionStateSnapshot,
  type ExtensionStateSnapshot,
  type ExtensionStatusCheck,
} from "./extensions/installer.js";
import type { ExtensionAvailabilityResult, ExtensionId } from "./extensions/types.js";
import { findBuiltinExtensionToolContribution } from "./extensions/tool-manifest.js";
import { currentInstanceProjectPath } from "./instances.js";
import type { RootAdmissionLease } from "./bridge/index.js";
import { generatedToolCompletionContract } from "./tools/.generated-completion-registry.js";
import type { ResolvedToolMetadata, ToolMetadata } from "./tools/metadata/types.js";

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

/** 一次公开调用冻结 package/install 输入，并按 distinct extension 缓存 target proof。 */
export interface ExtensionAvailabilitySnapshot {
  registry: ExtensionRegistry;
  projectPath?: string;
  state?: ExtensionStateSnapshot;
  targetProofs: Map<ExtensionId, Promise<ExtensionStatusCheck | undefined>>;
}

type ExtensionAvailabilityRuntime = {
  dataDir?: string;
  rootAdmission?: RootAdmissionLease;
  rootAdmissions?: Partial<Record<"editor" | "runtime", RootAdmissionLease>>;
};

/** selector 解析、packages-lock 与 install manifest 在 invocation 起点只执行一次。 */
export async function freezeExtensionAvailability(
  flags: Record<string, string | boolean>,
  runtime: ExtensionAvailabilityRuntime,
): Promise<ExtensionAvailabilitySnapshot> {
  const resolvedFlags = await extensionAvailabilityFlags(flags, runtime);
  const projectPath = getString(resolvedFlags, "project");
  return {
    registry: new ExtensionRegistry(),
    projectPath,
    state: projectPath ? await readExtensionStateSnapshot(projectPath) : undefined,
    targetProofs: new Map(),
  };
}

/** selected dependency gate 复用 generated descriptor owner，不从名称前缀猜 Extension。 */
export function extensionIdForToolName(toolName: string): ExtensionId | undefined {
  const owner = generatedToolCompletionContract(toolName)?.descriptorOwner;
  return owner?.startsWith("extension:") ? owner.slice("extension:".length) as ExtensionId : undefined;
}

/** 普通 Tool invocation 至多证明其 selected extension，不扫描其他 extension。 */
export async function checkToolExtensionAvailability(
  tool: Pick<ToolMetadata, "name" | "requiresPackages"> & { source?: ToolMetadata["source"] },
  snapshot: ExtensionAvailabilitySnapshot,
  absoluteDeadlineMs?: number,
): Promise<ExtensionAvailabilityCheck> {
  const generatedExtensionId = extensionIdForToolName(tool.name);
  const contribution = findExtensionForTool(snapshot.registry, tool.name);
  if (!generatedExtensionId && !contribution && tool.source !== "extension") return available();
  if (!generatedExtensionId || !contribution || contribution.extensionId !== generatedExtensionId) {
    const extensionId = generatedExtensionId ?? contribution?.extensionId;
    return unavailable(extensionId, undefined, undefined, "E_EXTENSION_INVENTORY_MISMATCH", `Tool "${tool.name}" does not have one exact generated Extension owner matching its manifest contribution.`, [
      "Regenerate the unified Tool and Extension inventories, then let Unity compile and reload the domain.",
    ]);
  }
  const { extensionId } = contribution;
  const extension = snapshot.registry.getExtension(extensionId);
  if (!extension) return available();
  if (!snapshot.projectPath || !snapshot.state) {
    return unavailable(extensionId, undefined, undefined, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN", `Tool "${tool.name}" requires a project path to verify extension physical installation.`, [
      "Select the running Unity project with --instance <id|canonical-project-path|alias>.",
      `Run unitycli extensions doctor --project <path> to inspect ${extensionId}.`,
    ]);
  }

  const requiredPackageIds = [...new Set([...(tool.requiresPackages ?? []), ...contribution.requiresPackages])];
  const missingPackageIds = requiredPackageIds.filter((packageId) => {
    const actual = snapshot.state!.installedPackages.get(packageId);
    const expected = extension.manifest.unityPackages.find((pkg) => pkg.packageId === packageId)?.versionRange;
    return actual === undefined || expected !== undefined && actual !== expected;
  });
  const availability = snapshot.registry.resolveAvailability(extensionId, snapshot.state.installedPackages);
  if (missingPackageIds.length > 0) {
    const profiles = missingPackageIds.map((packageId) => {
      const requirement = extension.manifest.unityPackages.find((pkg) => pkg.packageId === packageId);
      const expected = requirement?.versionRange ? `${packageId}@${requirement.versionRange}` : packageId;
      const actual = snapshot.state!.installedPackages.get(packageId);
      return actual === undefined ? `${expected} (not installed)` : `${expected} (actual ${actual})`;
    });
    return unavailable(extensionId, { ...availability, missingPackageIds }, undefined, "E_EXTENSION_DEPENDENCY_MISSING", `Tool "${tool.name}" is unavailable because required Unity package profile(s) are unavailable: ${profiles.join(", ")}.`, availability.nextActions);
  }

  let proof = snapshot.targetProofs.get(extensionId);
  if (!proof) {
    proof = checkSelectedExtensionBundle(snapshot.projectPath, extensionId, { snapshot: snapshot.state, absoluteDeadlineMs });
    snapshot.targetProofs.set(extensionId, proof);
  }
  const physicalState = await proof;
  if (!physicalState) {
    return unavailable(extensionId, availability, undefined, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN", `Tool "${tool.name}" has no physical state record for extension "${extensionId}".`, [
      `Run unitycli extensions doctor --project ${snapshot.projectPath}.`,
    ]);
  }
  if (physicalState.installed && physicalState.dependencyInstalled && availability.status === "available") {
    return { available: true, extensionId, availabilityResult: availability, physicalState, nextActions: [], errorCode: "", message: "" };
  }
  const code = availability.status === "available" ? "E_EXTENSION_NOT_INSTALLED" : "E_EXTENSION_DEPENDENCY_MISSING";
  return unavailable(extensionId, availability, physicalState, code, `Tool "${tool.name}" is unavailable because extension files are ${physicalState.status}.`, physicalState.nextActions);
}

/** 一页 discovery 对同一 extension 复用一项 target proof。 */
export async function extensionAvailabilityForPage(
  tools: Array<Pick<ToolMetadata, "name" | "requiresPackages">>,
  snapshot: ExtensionAvailabilitySnapshot,
  absoluteDeadlineMs?: number,
): Promise<Map<string, ExtensionAvailabilityCheck>> {
  const result = new Map<string, ExtensionAvailabilityCheck>();
  for (const tool of tools) result.set(tool.name, await checkToolExtensionAvailability(tool, snapshot, absoluteDeadlineMs));
  return result;
}

/**
 * physical gate 优先使用显式 project，否则复用实例选择语义推断当前目标项目。
 * bridge endpoint 没有可用 registry 项目时保留原 flags，使调用方返回可恢复的 unknown。
 */
export async function extensionAvailabilityFlags(
  flags: Record<string, string | boolean>,
  runtime: ExtensionAvailabilityRuntime,
): Promise<Record<string, string | boolean>> {
  if (getString(flags, "project")) return flags;
  const admittedProject = runtime.rootAdmissions?.editor?.canonicalProjectPath
    ?? runtime.rootAdmissions?.runtime?.canonicalProjectPath
    ?? runtime.rootAdmission?.canonicalProjectPath;
  if (admittedProject) return { ...flags, project: admittedProject };
  if (!runtime.dataDir) return flags;
  const project = await currentInstanceProjectPath(flags, runtime.dataDir);
  return project ? { ...flags, project } : flags;
}

/** Extension 不提供离线 Tool descriptor；完整 descriptor/schema 只能由 live Bridge 返回。 */
export async function collectExtensionToolDescriptors(
  _flags: Record<string, string | boolean>,
): Promise<Array<ResolvedToolMetadata & { extensionId?: ExtensionId; extensionAvailable?: boolean }>> {
  return [];
}

function available(): ExtensionAvailabilityCheck {
  return { available: true, nextActions: [], errorCode: "", message: "" };
}

function unavailable(
  extensionId: ExtensionId | undefined,
  availabilityResult: ExtensionAvailabilityResult | undefined,
  physicalState: ExtensionStatusCheck | undefined,
  errorCode: string,
  message: string,
  nextActions: string[],
): ExtensionAvailabilityCheck {
  return { available: false, ...(extensionId ? { extensionId } : {}), availabilityResult, physicalState, errorCode, message, nextActions };
}

function findExtensionForTool(registry: ExtensionRegistry, toolName: string): { extensionId: ExtensionId; requiresPackages: string[] } | undefined {
  const contribution = findBuiltinExtensionToolContribution(toolName);
  if (contribution) return { extensionId: contribution.extensionId, requiresPackages: contribution.requiresPackages };
  const extensionId = registry.extensionIds().find((id) => registry.getExtension(id)!.manifest.contributions.tools?.some((tool) => tool.name === toolName));
  if (!extensionId) return undefined;
  const extension = registry.getExtension(extensionId)!;
  const tool = extension.manifest.contributions.tools?.find((item) => item.name === toolName);
  return { extensionId, requiresPackages: tool?.requiresPackages ?? extension.manifest.unityPackages.map((pkg) => pkg.packageId) };
}
