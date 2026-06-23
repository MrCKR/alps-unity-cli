/**
 * Extension contribution loader: registers ProBuilder, Cinemachine, VFX, URP,
 * HDRP and other built-in extension contributions into the core registries.
 *
 * Extension contributions (bridge specs, tool metadata) are loaded through
 * this module rather than being statically imported into the core aggregation
 * arrays. This separates the core/extension boundary so that:
 *
 * 1. Core metadata/registry does not directly aggregate extension tools
 * 2. Extension contributions are registered through the extension mechanism
 * 3. Core `tools group` / `tools group list` can filter extension tools by availability
 * 4. After extension registration, bridge routing and contract validation work normally
 */

import { probuilderBridgeSpecs } from "../bridge/domains/probuilder.js";
import { cinemachineBridgeSpecs } from "../bridge/domains/cinemachine.js";
import { vfxBridgeSpecs } from "../bridge/domains/vfx.js";
import { urpBridgeSpecs } from "../bridge/domains/urp.js";
import { hdrpBridgeSpecs } from "../bridge/domains/hdrp.js";
import { bridgeCommandRegistry } from "../bridge/index.js";
import { validateBridgeRegistryContracts } from "../contracts/command-contract.js";
import { PROBUILDER_TOOLS } from "../tools/metadata/groups/probuilder.js";
import { CINEMACHINE_TOOLS } from "../tools/metadata/groups/cinemachine.js";
import { VFX_TOOLS } from "../tools/metadata/groups/vfx.js";
import { URP_TOOLS } from "../tools/metadata/groups/urp.js";
import { HDRP_TOOLS } from "../tools/metadata/groups/hdrp.js";
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

  // Register extension tool metadata. User-facing execution always goes
  // through tools run, while argument builders preserve native argv mapping internally.
  registerExtensionTools(markToolsRunCommands([
    ...PROBUILDER_TOOLS,
    ...CINEMACHINE_TOOLS,
    ...VFX_TOOLS,
    ...URP_TOOLS,
    ...HDRP_TOOLS
  ]));

  // Validate extension contributions
  validateExtensionToolMetadata();

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

function markToolsRunCommands<T extends { name: string; cliCommand?: string }>(tools: T[]): T[] {
  return tools.map((tool) => ({ ...tool, cliCommand: `tools run ${tool.name}` }));
}
