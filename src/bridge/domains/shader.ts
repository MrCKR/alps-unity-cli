import { boolFlag, compactArgs, contentFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const shaderBridgeSpecs: BridgeCommandSpec[] = [
  { group: "shader", command: "info", bridgeCommand: "shader.info", buildArgs: shaderArgs },
  { group: "shader", command: "read", bridgeCommand: "shader.read", buildArgs: shaderArgs },
  { group: "shader", command: "create", bridgeCommand: "shader.create", buildArgs: shaderArgs },
  { group: "shader", command: "update", bridgeCommand: "shader.update", buildArgs: shaderArgs }
];

async function shaderArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Promise<Record<string, unknown>> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "shader"),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "shader"),
    name: stringFlag(flags, "name"),
    content: await contentFlag(flags),
    overwrite: boolFlag(flags, "overwrite")
  });
}
