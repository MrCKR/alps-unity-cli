import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { bridgeCommandRegistry } from "../bridge/index.js";
import { builtinToolMetadata } from "../tools/metadata/index.js";
import { commandContracts, validateBridgeRegistryContracts, validateToolMetadataContracts, validateExtensionManifestContracts } from "./command-contract.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../extensions/registry-data.js";

const contracts = commandContracts();
validateBridgeRegistryContracts(bridgeCommandRegistry);
validateToolMetadataContracts(builtinToolMetadata(), bridgeCommandRegistry);

// P5-T04: Validate extension manifest tool contributions against contract
validateExtensionManifestContracts(BUILTIN_EXTENSION_MANIFESTS, bridgeCommandRegistry);

const csharpCommands = extractCSharpRegisteredCommands("unitypkg/Editor");
const requiredCsharpCommands = contracts
  .filter((entry) => (entry.handler_type === "bridge" || entry.handler_type === "hybrid") && entry.csharp_handler === "present")
  .map((entry) => entry.bridgeCommand)
  .sort();

assertSameSet(requiredCsharpCommands, csharpCommands, "C# BridgeCommandRegistry");

console.log(JSON.stringify({
  ok: true,
  contractCommands: contracts.length,
  bridgeRegistryCommands: new Set(bridgeCommandRegistry.specs().map((spec) => spec.bridgeCommand)).size,
  csharpRegisteredCommands: csharpCommands.length,
  metadataTools: builtinToolMetadata().length
}, null, 2));

function extractCSharpRegisteredCommands(root: string): string[] {
  const commands = new Set<string>();
  for (const file of listFiles(root, ".cs")) {
    const source = readFileSync(file, "utf8");
    for (const match of source.matchAll(/RegisterCommand\(registry, "([^"]+)"/g)) {
      commands.add(match[1]);
    }
  }
  return [...commands].sort();
}

function listFiles(directory: string, extension: string): string[] {
  const entries = readdirSync(directory, { withFileTypes: true });
  return entries.flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return listFiles(path, extension);
    }
    return entry.isFile() && entry.name.endsWith(extension) ? [path] : [];
  });
}

function assertSameSet(expected: string[], actual: string[], label: string): void {
  const actualSet = new Set(actual);
  const expectedSet = new Set(expected);
  const missing = expected.filter((item) => !actualSet.has(item));
  const extra = actual.filter((item) => !expectedSet.has(item));
  if (missing.length > 0 || extra.length > 0) {
    throw new Error(`${label} contract mismatch. Missing: ${missing.join(", ") || "<none>"}; Extra: ${extra.join(", ") || "<none>"}`);
  }
}
