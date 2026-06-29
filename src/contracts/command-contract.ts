import { existsSync, readFileSync } from "node:fs";
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
const contractFile = loadCommandContractFile(contractPath);
const contractByBridgeCommand = new Map<string, CommandContractEntry>();

// 加载命令契约；生成契约时允许空启动,避免生成器与产物互相依赖。
function loadCommandContractFile(path: string): CommandContractFile {
  if (process.env.UNITY_CLI_GENERATING_CONTRACT === "1" && !existsSync(path)) {
    return { version: 1, generatedFrom: "command contract generation bootstrap", commands: [] };
  }
  return JSON.parse(readFileSync(path, "utf8")) as CommandContractFile;
}

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
    if (contract.destructive !== tool.destructiveHint) {
      throw new Error(`Tool metadata destructive hint mismatch for ${tool.name}`);
    }
    if (JSON.stringify(contract.requires_packages ?? []) !== JSON.stringify(tool.requiresPackages ?? [])) {
      throw new Error(`Tool metadata requiresPackages mismatch for ${tool.name}: metadata=${JSON.stringify(tool.requiresPackages ?? [])} contract=${JSON.stringify(contract.requires_packages ?? [])}`);
    }
    if (tool.cliCommand && (tool.handler === "bridge" || tool.handler === "hybrid")) {
      // cliCommand 现为 "tools run <name>" 形态(README Project Baseline)。
      const trMatch = /^tools run (.+)$/.exec(tool.cliCommand);
      if (trMatch) {
        const toolName = trMatch[1];
        const spec = registry.specs().find((s) => s.toolName === toolName || s.bridgeCommand === toolName) as BridgeCommandSpec | undefined;
        const resolvedCommand = spec?.toolName ?? spec?.bridgeCommand;
        if (spec && resolvedCommand !== tool.name) {
          throw new Error(`Tool metadata contract mismatch: ${tool.name} uses cliCommand="${tool.cliCommand}" but registry resolves ${resolvedCommand}`);
        }
      }
      // 非 "tools run" 形态(如 tools group/describe、init)跳过 bridge 交叉校验。
    }
  }
}

export function validateExtensionManifestContracts(
  manifests: ExtensionManifest[],
  registry: BridgeCommandRegistry,
  toolMetadata: ToolMetadata[] = []
): void {
  const metadataByName = new Map(toolMetadata.map((tool) => [tool.name, tool]));
  for (const manifest of manifests) {
    const tools = manifest.contributions.tools ?? [];

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

      // Tool-level metadata is the source of truth for optional Unity package needs.
      // A single extension bundle may contain built-in Unity component tools and
      // package-specific tools, so extension-level packages are not inherited here.
      const expectedPackages = (metadataByName.get(tool.name)?.requiresPackages ?? tool.requiresPackages ?? manifest.unityPackages.map((p) => p.packageId)).slice().sort();
      const contractPackages = (contract.requires_packages ?? []).slice().sort();
      if (JSON.stringify(contractPackages) !== JSON.stringify(expectedPackages)) {
        throw new Error(
          `Extension '${manifest.id}' tool '${tool.name}' requires_packages mismatch: ` +
          `metadata=${JSON.stringify(expectedPackages)} contract=${JSON.stringify(contractPackages)}`
        );
      }

      // Cross-check the cliCommand resolves to the contract bridgeCommand.
      // cliCommand 现为 "tools run <name>" 形态(README Project Baseline)。
      if (tool.cliCommand) {
        const trMatch = /^tools run (.+)$/.exec(tool.cliCommand);
        if (trMatch) {
          const toolName = trMatch[1];
          const spec = registry.specs().find((s) => s.toolName === toolName || s.bridgeCommand === toolName) as BridgeCommandSpec | undefined;
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
}
