import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const vfxBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
  { group: "vfx", command: "graph", bridgeCommand: "project-tool.run", toolName: "vfx.graph", buildArgs: vfxGraphArgs },
  { group: "vfx", command: "effect", bridgeCommand: "project-tool.run", toolName: "vfx.effect", buildArgs: vfxEffectArgs },
  { group: "vfx", command: "parameter", bridgeCommand: "project-tool.run", toolName: "vfx.parameter", buildArgs: vfxParameterArgs },
  { group: "vfx", command: "control", bridgeCommand: "project-tool.run", toolName: "vfx.control", buildArgs: vfxControlArgs },
  { group: "vfx", command: "event", bridgeCommand: "project-tool.run", toolName: "vfx.event", buildArgs: vfxEventArgs },
  { group: "vfx", command: "template", bridgeCommand: "project-tool.run", toolName: "vfx.template", buildArgs: vfxTemplateArgs },
  { group: "vfx", command: "particle-create", bridgeCommand: "project-tool.run", toolName: "vfx.particle-create", buildArgs: vfxParticleArgs },
  { group: "vfx", command: "particle-info", bridgeCommand: "project-tool.run", toolName: "vfx.particle-info", buildArgs: vfxParticleInfoArgs },
  { group: "vfx", command: "particle-main", bridgeCommand: "project-tool.run", toolName: "vfx.particle-main", buildArgs: vfxParticleMainArgs },
  { group: "vfx", command: "particle-emission", bridgeCommand: "project-tool.run", toolName: "vfx.particle-emission", buildArgs: vfxParticleEmissionArgs },
  { group: "vfx", command: "particle-shape", bridgeCommand: "project-tool.run", toolName: "vfx.particle-shape", buildArgs: vfxParticleShapeArgs },
  { group: "vfx", command: "particle-play", bridgeCommand: "project-tool.run", toolName: "vfx.particle-play", buildArgs: vfxParticlePlayArgs },
  { group: "vfx", command: "particle-module", bridgeCommand: "project-tool.run", toolName: "vfx.particle-module", buildArgs: vfxParticleModuleArgs },
  { group: "vfx", command: "line-create", bridgeCommand: "project-tool.run", toolName: "vfx.line-create", buildArgs: vfxLineCreateArgs },
  { group: "vfx", command: "line-positions", bridgeCommand: "project-tool.run", toolName: "vfx.line-positions", buildArgs: vfxLinePositionsArgs },
  { group: "vfx", command: "trail", bridgeCommand: "project-tool.run", toolName: "vfx.trail", buildArgs: vfxTrailArgs }
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

function vfxGraphArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "graph") ?? stringValue(structured.path),
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? stringValue(structured.action) ?? (boolValue(structured.create) || flags.create ? "create" : undefined),
    create: boolValue(structured.create),
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured.overwrite)
  });
}

function vfxEffectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    graph: stringFlag(flags, "graph") ?? stringFlag(flags, "path") ?? stringValue(structured.graph),
    add: boolFlag(flags, "add") ?? boolValue(structured.add)
  });
}

function vfxParameterArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  const type = stringFlag(flags, "type") ?? stringFlag(flags, "param-type") ?? stringValue(structured.type);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    name: stringFlag(flags, "name") ?? stringValue(structured.name),
    type,
    numberValue: type === "float" ? floatFlag(flags, "value") ?? numberValue(structured.numberValue) ?? numberValue(structured.value) : numberValue(structured.numberValue),
    intValue: type === "int" ? numberFlag(flags, "value") ?? numberValue(structured.intValue) ?? numberValue(structured.value) : numberValue(structured.intValue),
    boolValue: type === "bool" ? boolFlag(flags, "value") ?? boolValue(structured.boolValue) ?? boolValue(structured.value) : boolValue(structured.boolValue),
    assetPath: stringFlag(flags, "value") ?? stringValue(structured.assetPath) ?? stringValue(structured.value),
    x: floatFlag(flags, "x") ?? numberValue(structured.x),
    y: floatFlag(flags, "y") ?? numberValue(structured.y),
    z: floatFlag(flags, "z") ?? numberValue(structured.z),
    w: floatFlag(flags, "w") ?? numberValue(structured.w),
    r: floatFlag(flags, "r") ?? numberValue(structured.r),
    g: floatFlag(flags, "g") ?? numberValue(structured.g),
    b: floatFlag(flags, "b") ?? numberValue(structured.b),
    a: floatFlag(flags, "a") ?? numberValue(structured.a)
  });
}

function vfxControlArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? stringValue(structured.action),
    rate: floatFlag(flags, "rate") ?? numberValue(structured.rate),
    seed: numberFlag(flags, "seed") ?? numberValue(structured.seed),
    resetSeedOnPlay: boolFlag(flags, "reset-seed-on-play") ?? boolValue(structured.resetSeedOnPlay)
  });
}

function vfxEventArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    eventName: stringFlag(flags, "event-name") ?? stringFlag(flags, "name") ?? stringValue(structured.eventName),
    attributeName: stringFlag(flags, "attribute-name") ?? stringValue(structured.attributeName),
    attributeX: floatFlag(flags, "attribute-x") ?? numberValue(structured.attributeX),
    attributeY: floatFlag(flags, "attribute-y") ?? numberValue(structured.attributeY),
    attributeZ: floatFlag(flags, "attribute-z") ?? numberValue(structured.attributeZ),
    attributeSize: floatFlag(flags, "attribute-size") ?? numberValue(structured.attributeSize)
  });
}

function vfxTemplateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringValue(structured.path),
    template: stringFlag(flags, "template") ?? stringValue(structured.template),
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured.overwrite)
  });
}

function vfxParticleArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
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
  const structured = structuredInput(flags);
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target) });
}

function vfxParticleMainArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    duration: floatFlag(flags, "duration") ?? numberValue(structured.duration),
    startLifetime: floatFlag(flags, "start-lifetime") ?? numberValue(structured.startLifetime),
    startSpeed: floatFlag(flags, "start-speed") ?? numberValue(structured.startSpeed),
    startSize: floatFlag(flags, "start-size") ?? numberValue(structured.startSize),
    startColor: stringFlag(flags, "start-color") ?? stringValue(structured.startColor),
    startRotation: floatFlag(flags, "start-rotation") ?? numberValue(structured.startRotation),
    gravityModifier: floatFlag(flags, "gravity-modifier") ?? numberValue(structured.gravityModifier),
    maxParticles: numberFlag(flags, "max-particles") ?? numberValue(structured.maxParticles),
    loop: boolFlag(flags, "loop") ?? boolValue(structured.loop),
    playOnAwake: boolFlag(flags, "play-on-awake") ?? boolValue(structured.playOnAwake),
    simulationSpace: stringFlag(flags, "simulation-space") ?? stringValue(structured.simulationSpace),
    scalingMode: stringFlag(flags, "scaling-mode") ?? stringValue(structured.scalingMode)
  });
}

function vfxParticleEmissionArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    enabled: boolFlag(flags, "enabled") ?? boolValue(structured.enabled),
    rateOverTime: floatFlag(flags, "rate-over-time") ?? numberValue(structured.rateOverTime),
    rateOverDistance: floatFlag(flags, "rate-over-distance") ?? numberValue(structured.rateOverDistance),
    burstCount: numberFlag(flags, "burst-count") ?? numberValue(structured.burstCount),
    bursts: arrayValue(structured.bursts)
  });
}

function vfxParticleShapeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    enabled: boolFlag(flags, "enabled") ?? boolValue(structured.enabled),
    shapeType: stringFlag(flags, "shape-type") ?? stringValue(structured.shapeType),
    radius: floatFlag(flags, "radius") ?? numberValue(structured.radius),
    angle: floatFlag(flags, "angle") ?? numberValue(structured.angle),
    arc: floatFlag(flags, "arc") ?? numberValue(structured.arc),
    length: floatFlag(flags, "length") ?? numberValue(structured.length),
    boxX: floatFlag(flags, "box-x") ?? numberValue(structured.boxX),
    boxY: floatFlag(flags, "box-y") ?? numberValue(structured.boxY),
    boxZ: floatFlag(flags, "box-z") ?? numberValue(structured.boxZ),
    randomDirection: boolFlag(flags, "random-direction") ?? boolValue(structured.randomDirection)
  });
}

function vfxParticlePlayArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringValue(structured.target),
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? stringValue(structured.action),
    withChildren: boolFlag(flags, "with-children") ?? boolValue(structured.withChildren),
    stopBehavior: stringFlag(flags, "stop-behavior") ?? stringValue(structured.stopBehavior)
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
    subEmitterProperties: stringFlag(flags, "sub-emitter-properties") ?? stringValue(structured.subEmitterProperties),
    index: numberFlag(flags, "index") ?? numberValue(structured.index),
    mode: stringFlag(flags, "mode") ?? stringValue(structured.mode),
    curve: floatFlag(flags, "curve") ?? numberValue(structured.curve),
    speed: floatFlag(flags, "speed") ?? numberValue(structured.speed)
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
    widthCurve: parseCurveFlag(stringFlag(flags, "width-curve")) ?? arrayValue(structured.widthCurve),
    startColor: stringFlag(flags, "start-color") ?? stringValue(structured.startColor),
    endColor: stringFlag(flags, "end-color") ?? stringValue(structured.endColor),
    colorGradient: parseGradientFlag(stringFlag(flags, "color-gradient")) ?? arrayValue(structured.colorGradient),
    material: stringFlag(flags, "material") ?? stringValue(structured.material),
    positionCount: numberFlag(flags, "position-count") ?? numberValue(structured.positionCount),
    positions: parseVector3ArrayFlag(stringFlag(flags, "positions")) ?? arrayValue(structured.positions),
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
    positions: parseVector3ArrayFlag(stringFlag(flags, "positions")) ?? arrayValue(structured.positions),
    index: numberFlag(flags, "index") ?? numberValue(structured.index),
    position: parseVector3Flag(stringFlag(flags, "position")) ?? objectValue(structured.position)
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
    widthCurve: parseCurveFlag(stringFlag(flags, "width-curve")) ?? arrayValue(structured.widthCurve),
    startColor: stringFlag(flags, "start-color") ?? stringValue(structured.startColor),
    endColor: stringFlag(flags, "end-color") ?? stringValue(structured.endColor),
    colorGradient: parseGradientFlag(stringFlag(flags, "color-gradient")) ?? arrayValue(structured.colorGradient),
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

function arrayValue(value: unknown): unknown[] | undefined {
  return Array.isArray(value) ? value : undefined;
}

function objectValue(value: unknown): Record<string, unknown> | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function parseVector3Flag(value: string | undefined): Record<string, number> | undefined {
  if (!value) return undefined;
  const parts = value.split(",").map((item) => Number.parseFloat(item.trim()));
  if (parts.length < 3 || parts.some((item) => !Number.isFinite(item))) return undefined;
  return { x: parts[0], y: parts[1], z: parts[2] };
}

function parseVector3ArrayFlag(value: string | undefined): Array<Record<string, number>> | undefined {
  if (!value) return undefined;
  const items = value.split(/\s+/).map((item) => item.trim()).filter(Boolean);
  const parsed = items.map((item) => parseVector3Flag(item.replace(/[\[\]()]/g, ""))).filter((item): item is Record<string, number> => item !== undefined);
  return parsed.length === 0 ? undefined : parsed;
}

function parseCurveFlag(value: string | undefined): Array<Record<string, number>> | undefined {
  if (!value) return undefined;
  const result: Array<Record<string, number>> = [];
  for (const part of value.split(",").map((item) => item.trim()).filter(Boolean)) {
    const [timeRaw, valueRaw] = part.split(":").map((item) => item.trim());
    const time = Number.parseFloat(timeRaw ?? "");
    const y = Number.parseFloat(valueRaw ?? "");
    if (Number.isFinite(time) && Number.isFinite(y)) {
      result.push({ time, value: y });
    }
  }
  return result.length === 0 ? undefined : result;
}

function parseGradientFlag(value: string | undefined): Array<Record<string, unknown>> | undefined {
  if (!value) return undefined;
  const result: Array<Record<string, unknown>> = [];
  for (const part of value.split(",").map((item) => item.trim()).filter(Boolean)) {
    const [timeRaw, colorRaw] = part.split(":").map((item) => item.trim());
    const time = Number.parseFloat(timeRaw ?? "");
    if (Number.isFinite(time) && colorRaw) {
      result.push({ time, color: colorRaw });
    }
  }
  return result.length === 0 ? undefined : result;
}
