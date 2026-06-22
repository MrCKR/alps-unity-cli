import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import type { BridgeCommandRegistry, BridgeCommandSpec } from "../bridge/index.js";
import type { ToolMetadata } from "../tools/metadata/types.js";
import type { ExtensionManifest } from "../extensions/types.js";

export type CommandHandlerType = "bridge" | "hybrid" | "job" | "offline" | "cli_only";

export interface CommandContractEntry {
  bridgeCommand: string;
  group: string;
  command: string;
  handler_type: CommandHandlerType;
  csharp_handler: "present" | "none" | "planned";
  readOnly: boolean;
  destructive: boolean;
  timeout: number;
  requires_packages?: string[];
}

interface CommandContractFile {
  version: number;
  generatedFrom: string;
  commands: CommandContractEntry[];
}

const contractPath = fileURLToPath(new URL("../../contracts/command-contract.json", import.meta.url));
const contractFile = JSON.parse(readFileSync(contractPath, "utf8")) as CommandContractFile;
const contractByBridgeCommand = new Map<string, CommandContractEntry>();

for (const entry of contractFile.commands) {
  if (contractByBridgeCommand.has(entry.bridgeCommand)) {
    throw new Error(`Duplicate command contract entry: ${entry.bridgeCommand}`);
  }
  contractByBridgeCommand.set(entry.bridgeCommand, entry);
}

export function commandContracts(): CommandContractEntry[] {
  return [...contractFile.commands];
}

export function findCommandContract(bridgeCommand: string): CommandContractEntry | undefined {
  return contractByBridgeCommand.get(bridgeCommand);
}

export function validateBridgeRegistryContracts(registry: BridgeCommandRegistry, skipGroups?: Set<string>): void {
  const registryCommands = new Set<string>();
  const registryToolNames = new Set<string>();
  for (const spec of registry.specs()) {
    if (skipGroups?.has(spec.group)) continue;
    registryCommands.add(spec.bridgeCommand);
    if (spec.toolName) registryToolNames.add(spec.toolName);
    const contract = findCommandContract(spec.toolName ?? spec.bridgeCommand);
    if (!contract) {
      throw new Error(`Bridge command missing from contract: ${spec.toolName ?? spec.bridgeCommand}`);
    }
  }

  for (const contract of commandContracts()) {
    if (skipGroups?.has(contract.group)) continue;
    if (contract.csharp_handler === "present" && (contract.handler_type === "bridge" || contract.handler_type === "hybrid") && !registryCommands.has(contract.bridgeCommand) && !registryToolNames.has(contract.bridgeCommand)) {
      throw new Error(`Contract command missing from BridgeCommandRegistry: ${contract.bridgeCommand}`);
    }
  }
}

export function validateToolMetadataContracts(tools: ToolMetadata[], registry: BridgeCommandRegistry): void {
  for (const tool of tools) {
    const contract = findCommandContract(tool.name);
    if (!contract) {
      throw new Error(`Tool metadata missing from contract: ${tool.name}`);
    }
    if (contract.handler_type !== tool.handler) {
      throw new Error(`Tool metadata handler mismatch for ${tool.name}: metadata=${tool.handler ?? "<none>"} contract=${contract.handler_type}`);
    }
    if (contract.readOnly !== tool.readOnlyHint || contract.destructive !== tool.destructiveHint) {
      throw new Error(`Tool metadata safety hint mismatch for ${tool.name}`);
    }
    if (JSON.stringify(contract.requires_packages ?? []) !== JSON.stringify(tool.requiresPackages ?? [])) {
      throw new Error(`Tool metadata requiresPackages mismatch for ${tool.name}: metadata=${JSON.stringify(tool.requiresPackages ?? [])} contract=${JSON.stringify(contract.requires_packages ?? [])}`);
    }
    if (tool.cliCommand && (tool.handler === "bridge" || tool.handler === "hybrid")) {
      const [group, ...commandParts] = tool.cliCommand.split(" ");
      const spec = registry.lookup(group, commandParts.join(" ")) as BridgeCommandSpec | undefined;
      const resolvedCommand = spec?.toolName ?? spec?.bridgeCommand;
      if (spec && resolvedCommand !== tool.name) {
        throw new Error(`Tool metadata contract mismatch: ${tool.name} uses cliCommand=\"${tool.cliCommand}\" but registry resolves ${resolvedCommand}`);
      }
    }
  }
}

/**
 * Validate that extension manifest tool contributions are consistent with the
 * command contract. Each tool contribution's `bridgeCommand` must either:
 * - Be present in the contract (already registered as a built-in tool), or
 * - Declare a matching `bridgeCommand`/`cliCommand`/`group` tuple.
 *
 * Extension tool contributions that duplicate built-in tool names must
 * match the built-in contract exactly.
 *
 * @param manifests - Extension manifests to validate.
 * @param registry - Bridge command registry for cross-checking cliCommand.
 */
export function validateExtensionManifestContracts(
  manifests: ExtensionManifest[],
  registry: BridgeCommandRegistry
): void {
  for (const manifest of manifests) {
    const tools = manifest.contributions.tools ?? [];
    const expectedPackages = manifest.unityPackages.map((p) => p.packageId).sort();

    for (const tool of tools) {
      const contract = findCommandContract(tool.bridgeCommand);
      if (!contract) {
        // Extension contributes a tool that is not in the contract.
        // This is allowed only if the extension declares it as a future
        // contribution (no live bridge command). We verify the bridgeCommand
        // group matches the manifest's declared bridgeCommands.
        const declaredBridge = (manifest.contributions.bridgeCommands ?? []).find(
          (b) => b.group === tool.group && b.commands.length > 0
        );
        if (!declaredBridge) {
          throw new Error(
            `Extension '${manifest.id}' contributes tool '${tool.name}' ` +
            `with bridgeCommand '${tool.bridgeCommand}' that is missing from the contract ` +
            `and not declared in manifest.bridgeCommands.`
          );
        }
        continue;
      }

      // Contract exists — verify the manifest's tool/group matches it.
      if (contract.group !== tool.group) {
        throw new Error(
          `Extension '${manifest.id}' tool '${tool.name}' group mismatch: ` +
          `manifest=${tool.group} contract=${contract.group}`
        );
      }

      // Verify the contract's requires_packages includes the extension's
      // declared Unity packages (so the contract is the source of truth).
      const contractPackages = (contract.requires_packages ?? []).slice().sort();
      if (JSON.stringify(contractPackages) !== JSON.stringify(expectedPackages)) {
        throw new Error(
          `Extension '${manifest.id}' tool '${tool.name}' requires_packages mismatch: ` +
          `manifest=${JSON.stringify(expectedPackages)} contract=${JSON.stringify(contractPackages)}`
        );
      }

      // Cross-check the cliCommand resolves to the contract bridgeCommand.
      if (tool.cliCommand) {
        const [group, ...commandParts] = tool.cliCommand.split(" ");
        const spec = registry.lookup(group, commandParts.join(" ")) as BridgeCommandSpec | undefined;
        const specResolvedCommand = spec?.toolName ?? spec?.bridgeCommand;
        if (spec && specResolvedCommand !== tool.bridgeCommand) {
          throw new Error(
            `Extension '${manifest.id}' tool '${tool.name}' cliCommand mismatch: ` +
            `cliCommand="${tool.cliCommand}" resolves to ${specResolvedCommand}, ` +
            `expected ${tool.bridgeCommand}`
          );
        }
      }
    }
  }
}
