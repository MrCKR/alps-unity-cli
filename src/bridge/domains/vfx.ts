import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const vfxBridgeSpecs: BridgeCommandSpec[] = [
  { group: "vfx", command: "graph", bridgeCommand: "vfx.graph", buildArgs: vfxGraphArgs },
  { group: "vfx", command: "effect", bridgeCommand: "vfx.effect", buildArgs: vfxEffectArgs },
  { group: "vfx", command: "parameter", bridgeCommand: "vfx.parameter", buildArgs: vfxParameterArgs },
  { group: "vfx", command: "control", bridgeCommand: "vfx.control", buildArgs: vfxControlArgs },
  { group: "vfx", command: "event", bridgeCommand: "vfx.event", buildArgs: vfxEventArgs },
  { group: "vfx", command: "template", bridgeCommand: "vfx.template", buildArgs: vfxTemplateArgs }
];

function vfxGraphArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? (flags.create ? "create" : "info"),
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "graph"),
    overwrite: boolFlag(flags, "overwrite")
  });
}

function vfxEffectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    graph: stringFlag(flags, "graph") ?? stringFlag(flags, "path"),
    add: boolFlag(flags, "add")
  });
}

function vfxParameterArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    name: stringFlag(flags, "name"),
    type: stringFlag(flags, "type") ?? stringFlag(flags, "param-type"),
    value: stringFlag(flags, "value"),
    x: floatFlag(flags, "x"),
    y: floatFlag(flags, "y"),
    z: floatFlag(flags, "z"),
    w: floatFlag(flags, "w"),
    r: floatFlag(flags, "r"),
    g: floatFlag(flags, "g"),
    b: floatFlag(flags, "b"),
    a: floatFlag(flags, "a")
  });
}

function vfxControlArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op"),
    rate: floatFlag(flags, "rate"),
    seed: numberFlag(flags, "seed"),
    resetSeedOnPlay: boolFlag(flags, "reset-seed-on-play")
  });
}

function vfxEventArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    eventName: stringFlag(flags, "event-name") ?? stringFlag(flags, "name"),
    attributeName: stringFlag(flags, "attribute-name"),
    attributeX: floatFlag(flags, "attribute-x"),
    attributeY: floatFlag(flags, "attribute-y"),
    attributeZ: floatFlag(flags, "attribute-z"),
    attributeSize: floatFlag(flags, "attribute-size")
  });
}

function vfxTemplateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path"),
    template: stringFlag(flags, "template"),
    overwrite: boolFlag(flags, "overwrite")
  });
}
