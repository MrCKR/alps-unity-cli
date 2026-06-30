import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import type { BridgeCommandRegistry, BridgeCommandSpec } from "../bridge/index.js";
import { toolCliCommand } from "../tools/metadata/derived.js";
import { toolHandler } from "../tools/metadata/status.js";
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
    const handler = toolHandler(tool);
    if (contract.handler_type !== handler) {
      throw new Error(`Tool metadata handler mismatch for ${tool.name}: metadata=${handler} contract=${contract.handler_type}`);
    }
    if (contract.destructive !== tool.destructiveHint) {
      throw new Error(`Tool metadata destructive hint mismatch for ${tool.name}`);
    }
    if (JSON.stringify(contract.requires_packages ?? []) !== JSON.stringify(tool.requiresPackages ?? [])) {
      throw new Error(`Tool metadata requiresPackages mismatch for ${tool.name}: metadata=${JSON.stringify(tool.requiresPackages ?? [])} contract=${JSON.stringify(contract.requires_packages ?? [])}`);
    }
    if (handler === "bridge" || handler === "hybrid") {
      const cliCommand = toolCliCommand(tool);
      // cliCommand 现为 "tools run <name>" 形态(README Project Baseline)。
      const trMatch = /^tools run (.+)$/.exec(cliCommand);
      if (trMatch) {
        const toolName = trMatch[1];
        const spec = registry.specs().find((s) => s.toolName === toolName || s.bridgeCommand === toolName) as BridgeCommandSpec | undefined;
        const resolvedCommand = spec?.toolName ?? spec?.bridgeCommand;
        if (spec && resolvedCommand !== tool.name) {
          throw new Error(`Tool metadata contract mismatch: ${tool.name} uses cliCommand="${cliCommand}" but registry resolves ${resolvedCommand}`);
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

    for (const contribution of tools) {
      const metadata = metadataByName.get(contribution.name);
      if (!metadata) {
        throw new Error(`Extension '${manifest.id}' contributes unknown tool '${contribution.name}' with no tool metadata.`);
      }

      const contract = findCommandContract(contribution.name);
      if (!contract) {
        throw new Error(`Extension '${manifest.id}' contributes tool '${contribution.name}' that is missing from the contract.`);
      }

      if (contract.group !== metadata.group) {
        throw new Error(
          `Extension '${manifest.id}' tool '${contribution.name}' group mismatch: ` +
          `metadata=${metadata.group} contract=${contract.group}`
        );
      }

      // Tool-level metadata is the source of truth for optional Unity package needs.
      // A single extension bundle may contain built-in Unity component tools and
      // package-specific tools, so extension-level packages are not inherited here.
      const expectedPackages = (metadata.requiresPackages ?? contribution.requiresPackages ?? manifest.unityPackages.map((p) => p.packageId)).slice().sort();
      const contractPackages = (contract.requires_packages ?? []).slice().sort();
      if (JSON.stringify(contractPackages) !== JSON.stringify(expectedPackages)) {
        throw new Error(
          `Extension '${manifest.id}' tool '${contribution.name}' requires_packages mismatch: ` +
          `metadata=${JSON.stringify(expectedPackages)} contract=${JSON.stringify(contractPackages)}`
        );
      }

      const spec = registry.specs().find((s) => s.toolName === contribution.name || s.bridgeCommand === contribution.name) as BridgeCommandSpec | undefined;
      const resolvedCommand = spec?.toolName ?? spec?.bridgeCommand;
      if (!spec || resolvedCommand !== contribution.name) {
        throw new Error(`Extension '${manifest.id}' tool '${contribution.name}' is missing matching bridge registry routing.`);
      }
    }
  }
}
