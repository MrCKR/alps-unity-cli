import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const vfxBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
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
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

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
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured.target),
    name: stringFlag(flags, "name") ?? stringValue(structured.name),
    duration: floatFlag(flags, "duration") ?? numberValue(structured.duration),
    startLifetime: floatFlag(flags, "start-lifetime") ?? numberValue(structured.startLifetime),
    startSpeed: floatFlag(flags, "start-speed") ?? numberValue(structured.startSpeed),
    startSize: floatFlag(flags, "start-size") ?? numberValue(structured.startSize),
    startColor: stringFlag(flags, "start-color") ?? stringValue(structured.startColor),
    maxParticles: numberFlag(flags, "max-particles") ?? numberValue(structured.maxParticles),
    loop: boolFlag(flags, "loop") ?? boolValue(structured.loop)
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
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    module: stringFlag(flags, "module") ?? stringValue(structured.module),
    enabled: boolFlag(flags, "enabled") ?? boolValue(structured.enabled),
    color: stringFlag(flags, "color") ?? stringValue(structured.color),
    size: floatFlag(flags, "size") ?? numberValue(structured.size),
    rotation: floatFlag(flags, "rotation") ?? numberValue(structured.rotation),
    x: floatFlag(flags, "x") ?? numberValue(structured.x),
    y: floatFlag(flags, "y") ?? numberValue(structured.y),
    z: floatFlag(flags, "z") ?? numberValue(structured.z),
    space: stringFlag(flags, "space") ?? stringValue(structured.space),
    speedModifier: floatFlag(flags, "speed-modifier") ?? numberValue(structured.speedModifier),
    strength: floatFlag(flags, "strength") ?? numberValue(structured.strength),
    frequency: floatFlag(flags, "frequency") ?? numberValue(structured.frequency),
    scrollSpeed: floatFlag(flags, "scroll-speed") ?? numberValue(structured.scrollSpeed),
    octaveCount: numberFlag(flags, "octave-count") ?? numberValue(structured.octaveCount),
    quality: stringFlag(flags, "quality") ?? stringValue(structured.quality),
    collisionType: stringFlag(flags, "collision-type") ?? stringValue(structured.collisionType),
    collisionMode: stringFlag(flags, "collision-mode") ?? stringValue(structured.collisionMode),
    dampen: floatFlag(flags, "dampen") ?? numberValue(structured.dampen),
    bounce: floatFlag(flags, "bounce") ?? numberValue(structured.bounce),
    lifetimeLoss: floatFlag(flags, "lifetime-loss") ?? numberValue(structured.lifetimeLoss),
    minKillSpeed: floatFlag(flags, "min-kill-speed") ?? numberValue(structured.minKillSpeed),
    maxKillSpeed: floatFlag(flags, "max-kill-speed") ?? numberValue(structured.maxKillSpeed),
    radiusScale: floatFlag(flags, "radius-scale") ?? numberValue(structured.radiusScale),
    ratio: floatFlag(flags, "ratio") ?? numberValue(structured.ratio),
    lifetime: floatFlag(flags, "lifetime") ?? numberValue(structured.lifetime),
    width: floatFlag(flags, "width") ?? numberValue(structured.width),
    subEmitterAction: stringFlag(flags, "sub-emitter-action") ?? stringValue(structured.subEmitterAction),
    subEmitterTarget: stringFlag(flags, "sub-emitter-target") ?? stringValue(structured.subEmitterTarget),
    subEmitterType: stringFlag(flags, "sub-emitter-type") ?? stringValue(structured.subEmitterType),
    subEmitterProperties: stringFlag(flags, "sub-emitter-properties") ?? stringValue(structured.subEmitterProperties)
  });
}

function vfxLineCreateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured.target),
    name: stringFlag(flags, "name") ?? stringValue(structured.name),
    startWidth: floatFlag(flags, "start-width") ?? numberValue(structured.startWidth),
    endWidth: floatFlag(flags, "end-width") ?? numberValue(structured.endWidth),
    widthMultiplier: floatFlag(flags, "width-multiplier") ?? numberValue(structured.widthMultiplier),
    widthCurve: stringFlag(flags, "width-curve") ?? structured.widthCurve,
    startColor: stringFlag(flags, "start-color") ?? stringValue(structured.startColor),
    endColor: stringFlag(flags, "end-color") ?? stringValue(structured.endColor),
    colorGradient: stringFlag(flags, "color-gradient") ?? structured.colorGradient,
    material: stringFlag(flags, "material") ?? stringValue(structured.material),
    positionCount: numberFlag(flags, "position-count") ?? numberValue(structured.positionCount),
    positions: stringFlag(flags, "positions") ?? structured.positions,
    loop: boolFlag(flags, "loop") ?? boolValue(structured.loop),
    useWorldSpace: boolFlag(flags, "use-world-space") ?? boolValue(structured.useWorldSpace),
    alignment: stringFlag(flags, "alignment") ?? stringValue(structured.alignment),
    textureMode: stringFlag(flags, "texture-mode") ?? stringValue(structured.textureMode),
    numCornerVertices: numberFlag(flags, "num-corner-vertices") ?? numberValue(structured.numCornerVertices),
    numCapVertices: numberFlag(flags, "num-cap-vertices") ?? numberValue(structured.numCapVertices)
  });
}

function vfxLinePositionsArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    positions: stringFlag(flags, "positions") ?? structured.positions,
    index: numberFlag(flags, "index") ?? numberValue(structured.index),
    position: stringFlag(flags, "position") ?? structured.position
  });
}

function vfxTrailArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured.target),
    name: stringFlag(flags, "name") ?? stringValue(structured.name),
    create: boolFlag(flags, "create") ?? boolValue(structured.create),
    time: floatFlag(flags, "time") ?? numberValue(structured.time),
    startWidth: floatFlag(flags, "start-width") ?? numberValue(structured.startWidth),
    endWidth: floatFlag(flags, "end-width") ?? numberValue(structured.endWidth),
    widthMultiplier: floatFlag(flags, "width-multiplier") ?? numberValue(structured.widthMultiplier),
    widthCurve: stringFlag(flags, "width-curve") ?? structured.widthCurve,
    startColor: stringFlag(flags, "start-color") ?? stringValue(structured.startColor),
    endColor: stringFlag(flags, "end-color") ?? stringValue(structured.endColor),
    colorGradient: stringFlag(flags, "color-gradient") ?? structured.colorGradient,
    material: stringFlag(flags, "material") ?? stringValue(structured.material),
    minVertexDistance: floatFlag(flags, "min-vertex-distance") ?? numberValue(structured.minVertexDistance),
    autodestruct: boolFlag(flags, "autodestruct") ?? boolValue(structured.autodestruct),
    emitting: boolFlag(flags, "emitting") ?? boolValue(structured.emitting),
    alignment: stringFlag(flags, "alignment") ?? stringValue(structured.alignment),
    textureMode: stringFlag(flags, "texture-mode") ?? stringValue(structured.textureMode),
    numCornerVertices: numberFlag(flags, "num-corner-vertices") ?? numberValue(structured.numCornerVertices),
    numCapVertices: numberFlag(flags, "num-cap-vertices") ?? numberValue(structured.numCapVertices)
  });
}

/** 从 --input 保留数组/对象字段，供曲线、渐变和坐标数组透传到 Unity Bridge。 */
function structuredInput(flags: BridgeFlags): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function numberValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function boolValue(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}

