/**
 * Extension system public API.
 *
 * P5-T01: Extension package manifest / registry / descriptor loader.
 *
 * The core CLI package does not import or compile-time depend on any optional
 * Unity packages. Extensions declare their Unity package requirements via
 * manifest, and the registry resolves availability against a project's
 * installed packages.
 */

export { ExtensionRegistry, ExtensionRegistryError, resolveExtensionAvailability, validateManifest } from "./registry.js";
export {
  loadAllExtensionDescriptors,
  loadExtensionDescriptor,
  loadAvailableDescriptors,
  loadUnavailableDescriptors,
  collectAvailableTools,
  collectAllTools,
  collectAvailableContexts,
  collectAvailableDocs,
  collectAvailableCSharpEntries,
  extensionAvailabilitySummary
} from "./descriptor-loader.js";
export {
  PROBUILDER_MANIFEST,
  VFX_GRAPH_MANIFEST,
  CINEMACHINE_MANIFEST,
  URP_MANIFEST,
  HDRP_MANIFEST,
  BUILTIN_EXTENSION_MANIFESTS
} from "./registry-data.js";
export {
  readInstalledUnityPackages,
  resolveExtensionAvailabilityForProject
} from "./project-resolver.js";

export type {
  ExtensionId,
  ExtensionAvailability,
  UnityPackageRequirement,
  ExtensionToolContribution,
  ExtensionContextContribution,
  ExtensionDocsContribution,
  ExtensionCSharpEntry,
  ExtensionContributions,
  ExtensionManifest,
  PackageResolution,
  ExtensionAvailabilityResult,
  RegistryExtension,
  LoadedExtensionDescriptor
} from "./types.js";
