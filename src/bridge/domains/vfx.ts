import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const vfxBridgeSpecs: BridgeCommandSpec[] = [
  { group: "vfx", command: "graph", bridgeCommand: "vfx.graph", buildArgs: vfxGraphArgs },
  { group: "vfx", command: "effect", bridgeCommand: "vfx.effect", buildArgs: vfxEffectArgs },
  { group: "vfx", command: "parameter", bridgeCommand: "vfx.parameter", buildArgs: vfxParameterArgs },
  { group: "vfx", command: "control", bridgeCommand: "vfx.control", buildArgs: vfxControlArgs },
  { group: "vfx", command: "event", bridgeCommand: "vfx.event", buildArgs: vfxEventArgs },
  { group: "vfx", command: "template", bridgeCommand: "vfx.template", buildArgs: vfxTemplateArgs },
  { group: "vfx", command: "particle-create", bridgeCommand: "vfx.particle-create", buildArgs: vfxParticleArgs },
  { group: "vfx", command: "particle-info", bridgeCommand: "vfx.particle-info", buildArgs: vfxParticleInfoArgs },
  { group: "vfx", command: "particle-main", bridgeCommand: "vfx.particle-main", buildArgs: vfxParticleMainArgs },
  { group: "vfx", command: "particle-emission", bridgeCommand: "vfx.particle-emission", buildArgs: vfxParticleEmissionArgs },
  { group: "vfx", command: "particle-shape", bridgeCommand: "vfx.particle-shape", buildArgs: vfxParticleShapeArgs },
  { group: "vfx", command: "particle-play", bridgeCommand: "vfx.particle-play", buildArgs: vfxParticlePlayArgs },
  { group: "vfx", command: "particle-module", bridgeCommand: "vfx.particle-module", buildArgs: vfxParticleModuleArgs },
  { group: "vfx", command: "line-create", bridgeCommand: "vfx.line-create", buildArgs: vfxLineCreateArgs },
  { group: "vfx", command: "line-positions", bridgeCommand: "vfx.line-positions", buildArgs: vfxLinePositionsArgs },
  { group: "vfx", command: "trail", bridgeCommand: "vfx.trail", buildArgs: vfxTrailArgs }
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

function vfxParticleArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    name: stringFlag(flags, "name"),
    duration: floatFlag(flags, "duration"),
    startLifetime: floatFlag(flags, "start-lifetime"),
    startSpeed: floatFlag(flags, "start-speed"),
    maxParticles: numberFlag(flags, "max-particles"),
    loop: boolFlag(flags, "loop")
  });
}

function vfxParticleInfoArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") });
}

function vfxParticleMainArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    duration: floatFlag(flags, "duration"),
    startLifetime: floatFlag(flags, "start-lifetime"),
    startSpeed: floatFlag(flags, "start-speed"),
    startSize: floatFlag(flags, "start-size"),
    startColor: stringFlag(flags, "start-color"),
    startRotation: floatFlag(flags, "start-rotation"),
    gravityModifier: floatFlag(flags, "gravity-modifier"),
    maxParticles: numberFlag(flags, "max-particles"),
    loop: boolFlag(flags, "loop"),
    playOnAwake: boolFlag(flags, "play-on-awake"),
    simulationSpace: stringFlag(flags, "simulation-space"),
    scalingMode: stringFlag(flags, "scaling-mode")
  });
}

function vfxParticleEmissionArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    enabled: boolFlag(flags, "enabled"),
    rateOverTime: floatFlag(flags, "rate-over-time"),
    rateOverDistance: floatFlag(flags, "rate-over-distance"),
    burstCount: numberFlag(flags, "burst-count")
  });
}

function vfxParticleShapeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    enabled: boolFlag(flags, "enabled"),
    shapeType: stringFlag(flags, "shape-type"),
    radius: floatFlag(flags, "radius"),
    angle: floatFlag(flags, "angle"),
    arc: floatFlag(flags, "arc"),
    length: floatFlag(flags, "length"),
    boxX: floatFlag(flags, "box-x"),
    boxY: floatFlag(flags, "box-y"),
    boxZ: floatFlag(flags, "box-z"),
    randomDirection: boolFlag(flags, "random-direction")
  });
}

function vfxParticlePlayArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op"),
    withChildren: boolFlag(flags, "with-children"),
    stopBehavior: stringFlag(flags, "stop-behavior")
  });
}

function vfxParticleModuleArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    module: stringFlag(flags, "module"),
    enabled: boolFlag(flags, "enabled")
  });
}

function vfxLineCreateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    name: stringFlag(flags, "name"),
    startWidth: floatFlag(flags, "start-width"),
    endWidth: floatFlag(flags, "end-width"),
    startColor: stringFlag(flags, "start-color"),
    endColor: stringFlag(flags, "end-color"),
    positionCount: numberFlag(flags, "position-count"),
    loop: boolFlag(flags, "loop"),
    useWorldSpace: boolFlag(flags, "use-world-space")
  });
}

function vfxLinePositionsArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    positions: stringFlag(flags, "positions"),
    index: numberFlag(flags, "index"),
    position: stringFlag(flags, "position")
  });
}

function vfxTrailArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    name: stringFlag(flags, "name"),
    create: boolFlag(flags, "create"),
    time: floatFlag(flags, "time"),
    startWidth: floatFlag(flags, "start-width"),
    endWidth: floatFlag(flags, "end-width"),
    minVertexDistance: floatFlag(flags, "min-vertex-distance"),
    autodestruct: boolFlag(flags, "autodestruct"),
    emitting: boolFlag(flags, "emitting")
  });
}
