import { BUILTIN_EXTENSION_MANIFESTS } from "./registry-data.js";
import type { ExtensionId, ExtensionManifest, ExtensionToolContribution } from "./types.js";

export interface BuiltinExtensionToolContribution {
  extensionId: ExtensionId;
  extensionName: string;
  manifest: ExtensionManifest;
  contribution: ExtensionToolContribution;
  name: string;
  group: string;
  requiresPackages: string[];
}

export interface BuiltinExtensionToolGroup {
  extensionId: ExtensionId;
  extensionName: string;
  manifest: ExtensionManifest;
  group: string;
  toolCount: number;
}

/** 从 dotted tool name 推导 discovery group。 */
export function groupFromToolName(name: string): string {
  const [group] = name.split(".");
  return group || "tools";
}

/** 读取 extension tool 的真实包依赖；显式 requiresPackages 优先，否则回退到 extension-level 依赖。 */
export function requiredPackagesForContribution(
  manifest: ExtensionManifest,
  contribution: ExtensionToolContribution
): string[] {
  if (contribution.requiresPackages !== undefined) {
    return [...new Set(contribution.requiresPackages)].sort();
  }
  return [...new Set(manifest.unityPackages.map((pkg) => pkg.packageId))].sort();
}

/** 汇总所有 built-in extension manifests 贡献的 tool 名单。 */
export function builtinExtensionToolContributions(): BuiltinExtensionToolContribution[] {
  return BUILTIN_EXTENSION_MANIFESTS.flatMap((manifest) =>
    (manifest.contributions.tools ?? []).map((contribution) => ({
      extensionId: manifest.id,
      extensionName: manifest.name,
      manifest,
      contribution,
      name: contribution.name,
      group: groupFromToolName(contribution.name),
      requiresPackages: requiredPackagesForContribution(manifest, contribution)
    }))
  );
}

/** 查找某个 extension tool 的 manifest 贡献信息。 */
export function findBuiltinExtensionToolContribution(name: string): BuiltinExtensionToolContribution | undefined {
  return builtinExtensionToolContributions().find((tool) => tool.name === name);
}

/** 汇总 extension 级 group 视图，供离线 group discovery 使用。 */
export function builtinExtensionToolGroups(): BuiltinExtensionToolGroup[] {
  const groups = new Map<string, BuiltinExtensionToolGroup>();
  for (const tool of builtinExtensionToolContributions()) {
    const existing = groups.get(tool.group);
    if (existing) {
      existing.toolCount += 1;
      continue;
    }
    groups.set(tool.group, {
      extensionId: tool.extensionId,
      extensionName: tool.extensionName,
      manifest: tool.manifest,
      group: tool.group,
      toolCount: 1
    });
  }
  return [...groups.values()].sort((left, right) => left.group.localeCompare(right.group));
}

/** 读取所有 extension group 名称集合。 */
export function builtinExtensionGroupNames(): string[] {
  return builtinExtensionToolGroups().map((group) => group.group);
}
