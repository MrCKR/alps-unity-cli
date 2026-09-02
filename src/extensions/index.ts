/**
 * Extension system public API.
 *
 * The core CLI package does not import or compile-time depend on any optional
 * Unity packages. Extensions declare their Unity package requirements via
 * manifest, and the registry resolves availability against a project's
 * installed packages.
 *
 * Built-in extension contributions are auto-registered when this module is
 * imported so CLI and test environments have extension tools available without
 * explicit registration.
 */

export {
  ExtensionRegistry,
  ExtensionRegistryError,
  resolveExtensionAvailability,
  validateManifest
} from "./registry.js";
export {
  loadAllExtensionDescriptors,
  loadExtensionDescriptor,
  loadAvailableDescriptors,
  loadUnavailableDescriptors,
  collectAvailableTools,
  collectAllTools,
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
} from "./.generated-registry-data.js";
export {
  readInstalledUnityPackages,
  resolveExtensionAvailabilityForProject
} from "./project-resolver.js";
export {
  checkExtensionBundles,
  checkSelectedExtensionBundle,
  listExtensionBundles,
  readExtensionStateSnapshot
} from "./installer.js";
export {
  registerBuiltinExtensionContributions,
  isBuiltinExtensionContributionsRegistered
} from "./contribution-loader.js";

// Auto-register built-in extension contributions when the extension module is
// loaded so bridge specs and tool metadata exist before discovery or routing.
import { registerBuiltinExtensionContributions } from "./contribution-loader.js";
registerBuiltinExtensionContributions();

export type {
  ExtensionId,
  ExtensionAvailability,
  UnityPackageRequirement,
  ExtensionToolContribution,
  ExtensionCSharpEntry,
  ExtensionContributions,
  ExtensionUnityInstallLayout,
  ExtensionManifest,
  PackageResolution,
  ExtensionAvailabilityResult,
  RegistryExtension,
  LoadedExtensionDescriptor
} from "./types.js";
