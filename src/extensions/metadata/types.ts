/**
 * Extension metadata type for the Unity CLI.
 *
 * An Extension is an optional capability pack that provides additional
 * tools, contexts, or both. Extensions depend on optional Unity packages
 * (e.g. com.unity.probuilder) and are not part of the core CLI package.
 *
 * Defines the typed descriptor surface for extension metadata.
 */

export type ExtensionSource = "built-in" | "package";
export type ExtensionStatus = "installed" | "available" | "unavailable";
export type ExtensionInstallMode = "auto" | "manual";

export interface ExtensionMetadata {
  /** Extension identifier, e.g. 'probuilder'. Must be unique. */
  id: string;
  /** Short human-readable title, e.g. 'ProBuilder Extension'. */
  title: string;
  /** One-paragraph explanation of what the extension provides. */
  description: string;
  /** Discriminator: always 'extension' for ExtensionMetadata. */
  kind: "extension";
  /** Origin: 'built-in' (declared in core metadata) or 'package' (separate npm/Unity package). */
  source: ExtensionSource;
  /** Semantic version of the extension, e.g. '1.0.0'. */
  version?: string;
  /** Current install status. */
  status: ExtensionStatus;
  /** Tools provided by this extension (dotted names, e.g. ['probuilder.shape']). */
  providesTools?: string[];
  /** Contexts provided by this extension (dotted names, e.g. ['probuilder.mesh-info']). */
  providesContexts?: string[];
  /** Unity packages required for this extension to work. */
  requiresUnityPackages?: string[];
  /** How the extension is installed: 'auto' (detected during init) or 'manual' (explicit install command). */
  installMode: ExtensionInstallMode;
  /** CLI command to install this extension, e.g. 'extensions install probuilder'. */
  installCommand?: string;
  /** Suggested next actions after listing/describing the extension. */
  nextActions?: string[];
}
