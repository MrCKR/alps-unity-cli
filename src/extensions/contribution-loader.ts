/**
 * Extension contribution loader: registers ProBuilder, VFX, and other
 * built-in extension contributions into the core registries.
 *
 * P10-R3: Extension contributions (bridge specs, tool metadata) are loaded
 * through this module rather than being statically imported into the core
 * aggregation arrays. This separates the core/extension boundary so that:
 *
 * 1. Core metadata/registry does not directly aggregate extension tools
 * 2. Extension contributions are registered through the extension mechanism
 * 3. Core `tools group` / `tools list` can filter extension tools by availability
 * 4. After extension registration, bridge routing and contract validation work normally
 */

import { probuilderBridgeSpecs } from "../bridge/domains/probuilder.js";
import { vfxBridgeSpecs } from "../bridge/domains/vfx.js";
import { bridgeCommandRegistry } from "../bridge/index.js";
import { validateBridgeRegistryContracts } from "../contracts/command-contract.js";
import { PROBUILDER_TOOLS } from "../tools/metadata/groups/probuilder.js";
import { VFX_TOOLS } from "../tools/metadata/groups/vfx.js";
import { registerExtensionTools, validateExtensionToolMetadata, EXTENSION_GROUPS } from "../tools/metadata/index.js";

let registered = false;

/**
 * Register all built-in extension contributions into the core registries.
 *
 * This must be called once during CLI startup, before any tool discovery
 * or bridge command routing that depends on extension contributions.
 *
 * After registration, contract validation is run for the full set
 * (core + extension) to ensure consistency.
 */
export function registerBuiltinExtensionContributions(): void {
  if (registered) return;
  registered = true;

  // Register extension bridge specs into the core bridge registry
  bridgeCommandRegistry.registerSpecs(probuilderBridgeSpecs);
  bridgeCommandRegistry.registerSpecs(vfxBridgeSpecs);

  // Register extension tool metadata
  registerExtensionTools([...PROBUILDER_TOOLS, ...VFX_TOOLS]);

  // Validate extension contributions
  validateExtensionToolMetadata();

  // Full bridge registry validation (core + extension)
  validateBridgeRegistryContracts(bridgeCommandRegistry);
}

/**
 * Check whether built-in extension contributions have been registered.
 */
export function isBuiltinExtensionContributionsRegistered(): boolean {
  return registered;
}
