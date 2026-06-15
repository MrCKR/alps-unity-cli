import { compactArgs, queryArgs, screenshotArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const cameraBridgeSpecs: BridgeCommandSpec[] = [
  { group: "camera", command: "list", bridgeCommand: "camera.list", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "camera", command: "info", bridgeCommand: "camera.info", buildArgs: ({ flags, positionalTarget }) => compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") }) },
  { group: "camera", command: "screenshot", bridgeCommand: "camera.screenshot", buildArgs: ({ flags, positionalTarget }) => compactArgs({ ...screenshotArgs(flags, undefined), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") }) }
];
