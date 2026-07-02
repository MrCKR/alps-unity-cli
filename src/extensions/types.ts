/**
 * Extension system types for the Unity CLI.
 *
 * Defines the manifest schema, registry types, and descriptor loader types
 * for the extension package system.
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

/** A tool capability ID contributed by an extension. Full AI-facing metadata lives in extension tool metadata. */
export interface ExtensionToolContribution {
  /** Tool name (dotted), e.g. "probuilder.create-shape". */
  name: string;
  /** Unity packages required by this specific tool; omitted means the extension-level package gate applies. */
  requiresPackages?: string[];
}

/** A context descriptor contribution from an extension. */
export interface ExtensionContextContribution {
  /** Context key, e.g. "probuilder.scene". */
  key: string;
  /** Human-readable description of the context. */
  description: string;
  /** 可选发现分类；未提供时使用 dotted key 前缀。 */
  category?: string;
  /** context read 输入参数的 JSON Schema。 */
  inputSchema?: Record<string, unknown>;
  /** context read 输出结果的 JSON Schema。 */
  outputSchema?: Record<string, unknown>;
  /** Context handler 的执行目标。 */
  target?: "editor" | "runtime" | "both";
  /** Context 结果的可用范围。 */
  availabilityScope?: "editorSession" | "playModeSession" | "always";
  /** Play Mode 下的读取语义。 */
  playModeSupport?: "supported" | "runtime-only" | "session-affecting" | "editor-only" | "unknown";
  /** 描述读取成本和陈旧性的刷新行为。 */
  refreshBehavior?: string;
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
  /** C# implementation entry points. */
  csharpEntries?: ExtensionCSharpEntry[];
}

/** 投影到 Packages/com.unity_cli/Extensions/<id> 的 Unity 侧文件布局。 */
export interface ExtensionUnityInstallLayout {
  /** 布局标识；"directory" 表示以独立子目录安装。 */
  layout?: "directory";
  /** 相对 extensions/<id>/ 的源码路径，会复制到 Unity 扩展目录。 */
  include?: string[];
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
  /** Unity 侧扩展目录安装布局。 */
  unityInstall?: ExtensionUnityInstallLayout;
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
  /** C# implementation entries. */
  csharpEntries: ExtensionCSharpEntry[];
}
