import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import type { BridgeCommandRegistry, BridgeCommandSpec } from "../bridge/index.js";
import { toolPublicRoute } from "../tools/metadata/derived.js";
import { toolHandler } from "../tools/metadata/status.js";
import type { ToolMetadata } from "../tools/metadata/types.js";
import type { ExtensionManifest } from "../extensions/types.js";
import { staticCapabilityCatalog } from "../static-capability-catalog.js";

export type CommandHandlerType = "bridge" | "bridge-runtime" | "hybrid" | "offline" | "cli_only";

/** Bridge 内部路由的唯一清单；这些 command 永远不得进入 public command contract。 */
export const PRIVATE_BRIDGE_COMMANDS: ReadonlySet<string> = new Set([
  "internal.editor.state.snapshot",
  "internal.doctor.business",
  "internal.doctor.catalog",
  "internal.doctor.compile",
  "internal.instances.prune",
  "internal.editor.compile.start",
  "internal.editor.compile.status",
  "internal.build.run.start",
  "internal.editor.refresh.start",
  "internal.editor.refresh.status",
  "internal.asset.refresh.start",
  "internal.asset.refresh.status",
  "internal.package.embed.start",
  "internal.package.embed.status",
  "internal.package.resolve.start",
  "internal.package.resolve.status",
  "internal.package.files.claim",
  "internal.package.files.commit",
  "internal.package.files.prove",
  "internal.package.files.fail",
  "internal.test.run.start",
  "internal.test.run.status",
  "internal.navmesh.bake.start",
  "internal.navmesh.bake.status",
  "internal.navmesh.bake.plan",
  "internal.navmesh.bake.snapshot",
  "internal.navmesh.bake.cancel",
]);

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
  const staticProjectTools = new Set(staticCapabilityCatalog().tools.map((tool) => String(tool.name)));
  const registryCommands = new Set<string>();
  const registryToolNames = new Set<string>();
  for (const spec of registry.specs()) {
    if (skipGroups?.has(spec.group) || PRIVATE_BRIDGE_COMMANDS.has(spec.bridgeCommand)) continue;
    registryCommands.add(spec.bridgeCommand);
    if (spec.toolName) registryToolNames.add(spec.toolName);
    const contract = findCommandContract(spec.toolName ?? spec.bridgeCommand);
    if (!contract) {
      throw new Error(`Bridge command missing from contract: ${spec.toolName ?? spec.bridgeCommand}`);
    }
  }

  for (const contract of commandContracts()) {
    if (skipGroups?.has(contract.group)) continue;
    if (contract.csharp_handler === "present" && (contract.handler_type === "bridge" || contract.handler_type === "hybrid") && !registryCommands.has(contract.bridgeCommand) && !registryToolNames.has(contract.bridgeCommand) && !staticProjectTools.has(contract.bridgeCommand)) {
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
      const publicRoute = toolPublicRoute(tool);
      const trMatch = /^tools run (.+)$/.exec(publicRoute);
      if (trMatch) {
        const toolName = trMatch[1];
        const spec = registry.specs().find((s) => s.toolName === toolName || s.bridgeCommand === toolName) as BridgeCommandSpec | undefined;
        const resolvedCommand = spec?.toolName ?? spec?.bridgeCommand;
        if (spec && resolvedCommand !== tool.name) {
          throw new Error(`Tool metadata contract mismatch: ${tool.name} uses public route "${publicRoute}" but registry resolves ${resolvedCommand}`);
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

      const contract = findCommandContract(contribution.name);
      if (!contract) {
        throw new Error(`Extension '${manifest.id}' contributes tool '${contribution.name}' that is missing from the contract.`);
      }

      const expectedGroup = metadata?.group ?? contribution.name.split(".")[0] ?? "extension";
      if (contract.group !== expectedGroup) {
        throw new Error(
          `Extension '${manifest.id}' tool '${contribution.name}' group mismatch: ` +
          `expected=${expectedGroup} contract=${contract.group}`
        );
      }

      // 新语义下，extension tool 的离线依赖真源在 manifest：
      // - 若 metadata/单 tool contribution 显式声明 requiresPackages，则 contract 必须对齐
      // - 若只存在 extension-level unityPackages，则不再强制 contract 重复该依赖
      const expectedPackages = metadata?.requiresPackages ?? contribution.requiresPackages;
      if (expectedPackages !== undefined && contract.requires_packages !== undefined) {
        const normalizedExpected = [...new Set(expectedPackages)].slice().sort();
        const contractPackages = contract.requires_packages.slice().sort();
        if (JSON.stringify(contractPackages) !== JSON.stringify(normalizedExpected)) {
          throw new Error(
            `Extension '${manifest.id}' tool '${contribution.name}' requires_packages mismatch: ` +
            `metadata=${JSON.stringify(normalizedExpected)} contract=${JSON.stringify(contractPackages)}`
          );
        }
      }

      const spec = registry.specs().find((s) => s.toolName === contribution.name || s.bridgeCommand === contribution.name) as BridgeCommandSpec | undefined;
      const resolvedCommand = spec?.toolName ?? spec?.bridgeCommand;
      if (!spec || resolvedCommand !== contribution.name) {
        throw new Error(`Extension '${manifest.id}' tool '${contribution.name}' is missing matching bridge registry routing.`);
      }
    }
  }
}
