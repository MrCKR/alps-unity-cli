/**
 * Extension contribution loader: registers ProBuilder, Cinemachine, VFX, URP,
 * HDRP and other built-in extension contributions into the core registries.
 *
 * Extension bridge specs are registered through this module. Tool ownership
 * comes from generated manifest projections, while full descriptors are read
 * from the live bridge. This separates the core/extension boundary so that:
 *
 * 1. Core metadata/registry does not directly aggregate extension tools
 * 2. Extension contributions are registered through the extension mechanism
 * 3. Core `tools group` / `tools group list` can filter extension tools by availability
 * 4. After extension registration, bridge routing and contract validation work normally
 */

import { probuilderBridgeSpecs } from "./.generated-bridge-domains/probuilder.js";
import { cinemachineBridgeSpecs } from "./.generated-bridge-domains/cinemachine.js";
import { vfxBridgeSpecs } from "./.generated-bridge-domains/vfx.js";
import { urpBridgeSpecs } from "./.generated-bridge-domains/urp.js";
import { hdrpBridgeSpecs } from "./.generated-bridge-domains/hdrp.js";
import { bridgeCommandRegistry } from "../bridge/index.js";
import { validateBridgeRegistryContracts } from "../contracts/command-contract.js";

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

  // Register extension bridge specs into the core bridge registry. They are
  // tools-only: top-level command families are blocked, but tools run reuses
  // their argument builders through nativeCliCommand.
  bridgeCommandRegistry.registerSpecs([
    ...markNativeCliCommands(probuilderBridgeSpecs),
    ...markNativeCliCommands(cinemachineBridgeSpecs),
    ...markNativeCliCommands(vfxBridgeSpecs),
    ...markNativeCliCommands(urpBridgeSpecs),
    ...markNativeCliCommands(hdrpBridgeSpecs)
  ]);

  // Tool names and package gates come from manifests; full descriptors come from the live bridge.

  // 生成 command-contract.json 时契约尚未完成,跳过完整反向校验。
  if (process.env.UNITY_CLI_GENERATING_CONTRACT !== "1") {
    validateBridgeRegistryContracts(bridgeCommandRegistry);
  }
}

/**
 * Check whether built-in extension contributions have been registered.
 */
export function isBuiltinExtensionContributionsRegistered(): boolean {
  return registered;
}

function markNativeCliCommands<T extends { group: string; command: string; nativeCliCommand?: string }>(specs: T[]): T[] {
  return specs.map((spec) => ({ ...spec, nativeCliCommand: spec.nativeCliCommand ?? `${spec.group} ${spec.command}` }));
}

