/**
 * Extension system types for the Unity CLI.
 *
 * Defines the manifest schema, registry types, and descriptor loader types
 * for the extension package system (P5-T01).
 *
 * Extensions are optional packages that contribute capabilities (tools,
 * bridge commands, context descriptors, docs fragments) to the CLI without
 * being compile-time dependencies of the core package.
 */

// ── Extension Manifest ──────────────────────────────────────────────────────

/** Unique extension identifier, e.g. "probuilder", "vfx-graph", "cinemachine". */
export type ExtensionId = string;

/** Status of an extension's availability in a given project. */
export type ExtensionAvailability =
  | "available"      // All required Unity packages are installed
  | "unavailable"    // One or more required packages are missing
  | "unknown";       // Cannot determine (e.g. no project path provided)

/** A single required Unity package and its resolved state. */
export interface UnityPackageRequirement {
  /** Unity package identifier, e.g. "com.unity.probuilder". */
  packageId: string;
  /** Minimum version constraint (semver range or exact version). Optional — absence means any version. */
  versionRange?: string;
  /** Human-readable name for the package, e.g. "ProBuilder". */
  displayName: string;
}

/** A tool contribution from an extension. */
export interface ExtensionToolContribution {
  /** Tool name (dotted), e.g. "probuilder.shape". */
  name: string;
  /** Human-readable short title. */
  title: string;
  /** One-paragraph description. */
  description: string;
  /** Logical group for tool discovery. */
  group: string;
  /** Bridge command name for execution routing. */
  bridgeCommand: string;
  /** When bridgeCommand is "project-tool.run", the underlying tool name. */
  toolName?: string;
  /** CLI argv prefix, e.g. "probuilder shape". */
  cliCommand: string;
}

/** A context descriptor contribution from an extension. */
export interface ExtensionContextContribution {
  /** Context key, e.g. "probuilder.scene". */
  key: string;
  /** Human-readable description of the context. */
  description: string;
}

/** A docs fragment contribution from an extension. */
export interface ExtensionDocsContribution {
  /** Documentation topic key, e.g. "probuilder". */
  topic: string;
  /** Short description of the documentation. */
  description: string;
}

/** A C# implementation entry contributed by an extension. */
export interface ExtensionCSharpEntry {
  /** Fully-qualified C# type name, e.g. "UnityCli.ProBuilderCommands". */
  type: string;
  /** Method name within the type. */
  method?: string;
  /** Description of the entry point. */
  description: string;
}

/** All contributions an extension can make to the CLI system. */
export interface ExtensionContributions {
  /** Tool descriptors contributed by this extension. */
  tools?: ExtensionToolContribution[];
  /** Context descriptors contributed by this extension. */
  contexts?: ExtensionContextContribution[];
  /** Bridge commands contributed by this extension (group → command list). */
  bridgeCommands?: Array<{ group: string; commands: string[] }>;
  /** C# implementation entry points. */
  csharpEntries?: ExtensionCSharpEntry[];
  /** Documentation fragment topics. */
  docs?: ExtensionDocsContribution[];
}

/** The top-level extension manifest. */
export interface ExtensionManifest {
  /** Unique extension identifier (kebab-case), e.g. "probuilder". */
  id: ExtensionId;
  /** Human-readable extension name. */
  name: string;
  /** One-paragraph description of the extension. */
  description: string;
  /** Extension version (semver). */
  version: string;
  /** Required Unity packages for this extension to be available. */
  unityPackages: UnityPackageRequirement[];
  /** Capabilities contributed by this extension. */
  contributions: ExtensionContributions;
  /** Tags for categorization, e.g. ["modeling", "3d"]. */
  tags?: string[];
  /** Homepage or documentation URL. */
  homepage?: string;
}

// ── Extension Registry ──────────────────────────────────────────────────────

/** Resolved state of a single Unity package within a project. */
export interface PackageResolution {
  /** Whether the package is present in the project's manifest/lock. */
  installed: boolean;
  /** Resolved version, if installed. */
  version?: string;
}

/** Resolved availability of an extension against a specific project. */
export interface ExtensionAvailabilityResult {
  /** The extension ID. */
  extensionId: ExtensionId;
  /** Overall availability status. */
  status: ExtensionAvailability;
  /** Per-package resolution details. */
  packages: Array<PackageResolution & { packageId: string; displayName: string }>;
  /** Missing package IDs (empty when status is "available"). */
  missingPackageIds: string[];
  /** Suggested next actions when status is "unavailable". */
  nextActions: string[];
}

/** An extension entry in the registry, combining manifest + resolved state. */
export interface RegistryExtension {
  /** The extension manifest. */
  manifest: ExtensionManifest;
  /** Whether this extension has built-in bridge domain support in the CLI. */
  hasBuiltinBridgeDomain: boolean;
}

// ── Descriptor Loader ───────────────────────────────────────────────────────

/** A loaded extension descriptor with resolved availability. */
export interface LoadedExtensionDescriptor {
  /** The extension manifest. */
  manifest: ExtensionManifest;
  /** Resolved availability against a project. */
  availability: ExtensionAvailabilityResult;
  /** Tool descriptors contributed by this extension. */
  tools: ExtensionToolContribution[];
  /** Context descriptors contributed by this extension. */
  contexts: ExtensionContextContribution[];
  /** Bridge command groups contributed. */
  bridgeCommands: Array<{ group: string; commands: string[] }>;
  /** C# implementation entries. */
  csharpEntries: ExtensionCSharpEntry[];
  /** Documentation topics. */
  docs: ExtensionDocsContribution[];
}
