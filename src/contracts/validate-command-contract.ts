import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { bridgeCommandRegistry } from "../bridge/index.js";
import { builtinToolMetadata, allRegisteredToolMetadata } from "../tools/metadata/index.js";
import { commandContracts, validateBridgeRegistryContracts, validateToolMetadataContracts, validateExtensionManifestContracts } from "./command-contract.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../extensions/registry-data.js";
import { registerBuiltinExtensionContributions } from "../extensions/contribution-loader.js";

// P10-R3: Register extension contributions before validation
registerBuiltinExtensionContributions();

const contracts = commandContracts();
validateBridgeRegistryContracts(bridgeCommandRegistry);
validateToolMetadataContracts(allRegisteredToolMetadata(), bridgeCommandRegistry);

// P5-T04: Validate extension manifest tool contributions against contract
validateExtensionManifestContracts(BUILTIN_EXTENSION_MANIFESTS, bridgeCommandRegistry);

const csharpCommands = extractAllCSharpRegisteredCommands();
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
  metadataTools: allRegisteredToolMetadata().length
}, null, 2));

function extractAllCSharpRegisteredCommands(): string[] {
  const commands = new Set(extractCSharpRegisteredCommands("unitypkg/Editor"));
  const extensionsRoot = "extensions";
  if (existsSync(extensionsRoot)) {
    for (const entry of readdirSync(extensionsRoot, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const editorRoot = join(extensionsRoot, entry.name, "unitypkg", "Editor");
      if (!existsSync(editorRoot)) continue;
      for (const command of extractCSharpRegisteredCommands(editorRoot)) {
        commands.add(command);
      }
    }
  }
  return [...commands].sort();
}

function extractCSharpRegisteredCommands(root: string): string[] {
  const commands = new Set<string>();
  for (const file of listFiles(root, ".cs")) {
    // Skip sample/demo tools — they are not part of the core contract
    if (file.includes("/Samples/") || file.includes("\\Samples\\")) continue;
    const source = readFileSync(file, "utf8");
    // Scan for RegisterCommand entries (core & legacy bridge commands)
    for (const match of source.matchAll(/RegisterCommand\(registry, "([^"]+)"/g)) {
      commands.add(match[1]);
    }
    // Scan for UnityCliTool attributes (P7 ProBuilder extension & P3 Custom Context tools).
    // These are discovered by ProjectToolRegistry at runtime, not registered via RegisterCommand.
    // Format: [UnityCliTool( "probuilder.ping", Title = ...
    for (const match of source.matchAll(/\[UnityCliTool\(\s*"([^"]+)"/g)) {
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
