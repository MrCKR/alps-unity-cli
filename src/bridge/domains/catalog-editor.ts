import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const catalogEditorBridgeSpecs: BridgeCommandSpec[] = [
  { group: "catalog", command: "editor.health", bridgeCommand: "catalog.editor.health", buildArgs: catalogEditorListArgs, internal: true },
  { group: "catalog", command: "editor.groups", bridgeCommand: "catalog.editor.groups", buildArgs: catalogEditorListArgs, internal: true },
  { group: "catalog", command: "editor.summary", bridgeCommand: "catalog.editor.summary", buildArgs: catalogEditorListArgs, internal: true },
  { group: "catalog", command: "editor.describe", bridgeCommand: "catalog.editor.describe", buildArgs: catalogEditorListArgs, internal: true },
  { group: "catalog", command: "editor.list", bridgeCommand: "catalog.editor.list", buildArgs: catalogEditorListArgs, internal: true },
  { group: "catalog", command: "editor.run", bridgeCommand: "catalog.editor.run", buildArgs: catalogEditorRunArgs, internal: true }
];

function catalogEditorListArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({});
}

async function catalogEditorRunArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Promise<Record<string, unknown>> {
  // The Bridge C# side uses RawJsonArg("input") which expects the input to be
  // a raw JSON value (object/array), not a stringified JSON string. Parse the
  // --input flag or read --input-file as JSON so it gets serialized as a raw
  // JSON object in the bridge request payload.
  const inputFilePath = stringFlag(flags, "input-file");
  let input: unknown;
  if (inputFilePath) {
    const content = await readFile(resolve(inputFilePath), "utf8");
    try {
      input = JSON.parse(content);
    } catch {
      input = content;
    }
  } else {
    const inputString = stringFlag(flags, "input");
    if (inputString !== undefined) {
      try {
        input = JSON.parse(inputString);
      } catch {
        // If it's not valid JSON, pass it as a string and let the Bridge
        // side handle the schema validation error.
        input = inputString;
      }
    }
  }

  return compactArgs({
    name: positionalTarget ?? stringFlag(flags, "name"),
    input,
    timeoutMs: numberFlag(flags, "timeout-ms")
  });
}
