/**
 * Static built-in extension metadata for the UnityCLI.
 *
 * 此处只提供 Extension package 的离线 metadata（标题、描述和 providesTools 名单）。
 * Extension Tool 的完整 descriptor/schema 由成功编译的 Live Editor 捕获到 static catalog，
 * 执行前仍以当前 Live Editor registration 为准。
 */

import { BUILTIN_EXTENSION_MANIFESTS } from "../.generated-registry-data.js";
import type { ExtensionMetadata } from "./types.js";

export type { ExtensionMetadata } from "./types.js";

const BUILTIN_EXTENSIONS: ExtensionMetadata[] = BUILTIN_EXTENSION_MANIFESTS.map((manifest) => ({
  id: manifest.id,
  title: `${manifest.name} Extension`,
  description: manifest.description,
  kind: "extension",
  source: "built-in",
  version: manifest.version,
  status: "available",
  providesTools: (manifest.contributions.tools ?? []).map((tool) => tool.name),
  requiresUnityPackages: manifest.unityPackages.map((pkg) => pkg.packageId),
  installMode: "auto",
  installCommand: `extensions install ${manifest.id}`,
  nextActions: [
    `Run unitycli extensions install ${manifest.id} to install this extension.`,
    "Run unitycli init to auto-detect and install extensions for your Unity project."
  ]
}));

/** Return all built-in extension metadata entries. */
export function builtinExtensionMetadata(): ExtensionMetadata[] {
  return BUILTIN_EXTENSIONS;
}

/** Find a single built-in extension by its id. */
export function findExtensionById(id: string): ExtensionMetadata | undefined {
  return BUILTIN_EXTENSIONS.find((e) => e.id === id);
}

/** Search extensions by keyword across id, title, description. */
export function searchExtensions(keyword: string): ExtensionMetadata[] {
  const lower = keyword.toLowerCase();
  return BUILTIN_EXTENSIONS.filter((e) =>
    e.id.toLowerCase().includes(lower) ||
    e.title.toLowerCase().includes(lower) ||
    e.description.toLowerCase().includes(lower)
  );
}
