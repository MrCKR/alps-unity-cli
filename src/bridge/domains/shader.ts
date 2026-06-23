import { boolFlag, compactArgs, contentFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const shaderBridgeSpecs: BridgeCommandSpec[] = [
  { group: "shader", command: "info", bridgeCommand: "shader.info", buildArgs: shaderArgs },
  { group: "shader", command: "read", bridgeCommand: "shader.read", buildArgs: shaderArgs },
  { group: "shader", command: "create", bridgeCommand: "shader.create", buildArgs: shaderArgs },
  { group: "shader", command: "update", bridgeCommand: "shader.update", buildArgs: shaderArgs },
  { group: "shader", command: "list", bridgeCommand: "shader.list", buildArgs: shaderListArgs },
  { group: "shader", command: "get-data", bridgeCommand: "shader.get-data", buildArgs: shaderArgs }
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

function shaderListArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ keyword: positionalTarget ?? stringFlag(flags, "keyword") ?? stringFlag(flags, "search"), pageSize: numberFlag(flags, "page-size") });
}
