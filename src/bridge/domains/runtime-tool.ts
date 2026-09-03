import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const runtimeToolBridgeSpecs: BridgeCommandSpec[] = [
  { group: "runtime-tool", command: "groups", bridgeCommand: "runtime-tool.groups", buildArgs: runtimeToolListArgs, internal: true },
  { group: "runtime-tool", command: "summary", bridgeCommand: "runtime-tool.summary", buildArgs: runtimeToolListArgs, internal: true },
  { group: "runtime-tool", command: "describe", bridgeCommand: "runtime-tool.describe", buildArgs: runtimeToolListArgs, internal: true },
  { group: "runtime-tool", command: "list", bridgeCommand: "runtime-tool.list", buildArgs: runtimeToolListArgs, internal: true },
  { group: "runtime-tool", command: "run", bridgeCommand: "runtime-tool.run", buildArgs: runtimeToolRunArgs, internal: true }
];

function runtimeToolListArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({});
}

async function runtimeToolRunArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Promise<Record<string, unknown>> {
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
    playModeSessionId: stringFlag(flags, "play-mode-session-id"),
    timeoutMs: numberFlag(flags, "timeout-ms"),
    artifactDir: stringFlag(flags, "artifact-dir")
  });
}
