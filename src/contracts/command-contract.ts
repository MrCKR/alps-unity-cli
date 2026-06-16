import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import type { BridgeCommandRegistry, BridgeCommandSpec } from "../bridge/index.js";
import type { ToolMetadata } from "../tools/metadata/types.js";

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
  architectureSensitive?: boolean;
  frozenDecision?: string;
  coverageScope?: string[];
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

export function validateBridgeRegistryContracts(registry: BridgeCommandRegistry): void {
  const registryCommands = new Set<string>();
  for (const spec of registry.specs()) {
    registryCommands.add(spec.bridgeCommand);
    const contract = findCommandContract(spec.bridgeCommand);
    if (!contract) {
      throw new Error(`Bridge command missing from contract: ${spec.bridgeCommand}`);
    }
  }

  for (const contract of commandContracts()) {
    if (contract.csharp_handler === "present" && (contract.handler_type === "bridge" || contract.handler_type === "hybrid") && !registryCommands.has(contract.bridgeCommand)) {
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
    if ((contract.architectureSensitive ?? false) !== (tool.architectureSensitive ?? false)) {
      throw new Error(`Tool metadata architecture sensitivity mismatch for ${tool.name}`);
    }
    if (contract.frozenDecision !== tool.frozenDecision) {
      throw new Error(`Tool metadata frozen decision mismatch for ${tool.name}`);
    }
    if (JSON.stringify(contract.coverageScope ?? []) !== JSON.stringify(tool.coverageScope ?? [])) {
      throw new Error(`Tool metadata coverage scope mismatch for ${tool.name}`);
    }
    if (tool.cliCommand && (tool.handler === "bridge" || tool.handler === "hybrid")) {
      const [group, ...commandParts] = tool.cliCommand.split(" ");
      const spec = registry.lookup(group, commandParts.join(" ")) as BridgeCommandSpec | undefined;
      if (spec && spec.bridgeCommand !== tool.name) {
        throw new Error(`Tool metadata contract mismatch: ${tool.name} uses cliCommand=\"${tool.cliCommand}\" but registry resolves ${spec.bridgeCommand}`);
      }
    }
  }
}
