import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

const VFX_GRAPH_PACKAGE = "com.unity.visualeffectgraph";

const VFX_GRAPH_COMMON = {
  group: "vfx",
  kind: "tool" as const,
  source: "extension" as const,
  target: "editor",
  availabilityScope: "editorSession",
  handler: "bridge" as const,
  docsTopic: "vfx",
  requiresPackages: [VFX_GRAPH_PACKAGE]
};

const BUILTIN_VFX_COMMON = {
  group: "vfx",
  kind: "tool" as const,
  source: "extension" as const,
  target: "editor",
  availabilityScope: "editorSession",
  handler: "bridge" as const,
  docsTopic: "vfx"
};

const vector3Properties = { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } };
const vector3Schema = objSchema(vector3Properties);
const curveKeySchema = objSchema({ time: { type: "number" }, value: { type: "number" } }, ["time", "value"]);
const gradientKeySchema = objSchema({ time: { type: "number" }, color: { type: "string" } }, ["time", "color"]);

/**
 * VFX 扩展 descriptor：VFX Graph 工具保留 tool-level 包依赖，Particle/Line/Trail 使用 Unity 内置组件。
 */
export const VFX_TOOLS: ToolMetadata[] = [
  {
    name: "vfx.graph",
    summary: "Get info on a Visual Effect graph asset, optionally create one.",
    description: "Get info on a Visual Effect graph asset, optionally create one. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    ...VFX_GRAPH_COMMON,
    inputSchema: objSchema({ path: { type: "string" }, action: { type: "string", enum: ["info", "create"] }, create: { type: "boolean" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.graph"
  },
  {
    name: "vfx.effect",
    summary: "Manage a Visual Effect instance on a GameObject.",
    description: "Manage a Visual Effect instance on a GameObject. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    ...VFX_GRAPH_COMMON,
    inputSchema: objSchema({ target: { type: "string" }, graph: { type: "string" }, add: { type: "boolean" } }, ["target"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.effect"
  },
  {
    name: "vfx.parameter",
    summary: "Set exposed parameters on a Visual Effect instance.",
    description: "Set exposed parameters on a Visual Effect instance: float, int, bool, vector2/3/4, color, texture, mesh, gradient, or curve. Requires com.unity.visualeffectgraph.",
    ...VFX_GRAPH_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      name: { type: "string" },
      type: { type: "string", enum: ["float", "int", "bool", "vector2", "vector3", "vector4", "color", "texture", "mesh", "gradient", "curve"] },
      value: { type: "string" },
      x: { type: "number" },
      y: { type: "number" },
      z: { type: "number" },
      w: { type: "number" },
      r: { type: "number" },
      g: { type: "number" },
      b: { type: "number" },
      a: { type: "number" }
    }, ["target", "name"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.parameter",
    examples: [
      { description: "Set an exposed float parameter", argv: ["tools", "run", "vfx.parameter", "--input", "{\"target\":\"VFXFire\",\"name\":\"Intensity\",\"type\":\"float\",\"value\":\"2.5\"}"] },
      { description: "Set a vector3 parameter", argv: ["tools", "run", "vfx.parameter", "--input", "{\"target\":\"VFXFire\",\"name\":\"Velocity\",\"type\":\"vector3\",\"x\":0,\"y\":1,\"z\":0}"] }
    ]
  },
  {
    name: "vfx.control",
    summary: "Control playback of a Visual Effect instance.",
    description: "Control playback of a Visual Effect instance: play, stop, pause, reinit, set play rate, or set seed. Requires com.unity.visualeffectgraph.",
    ...VFX_GRAPH_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      action: { type: "string", enum: ["play", "stop", "pause", "reinit", "rate", "seed"] },
      rate: { type: "number" },
      seed: { type: "integer" },
      resetSeedOnPlay: { type: "boolean" }
    }, ["target"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.control",
    examples: [
      { description: "Play a VFX effect", argv: ["tools", "run", "vfx.control", "--input", "{\"target\":\"VFXFire\",\"action\":\"play\"}"] }
    ]
  },
  {
    name: "vfx.event",
    summary: "Send a named event to a Visual Effect instance.",
    description: "Send a named event to a Visual Effect instance, optionally with attribute payloads such as position or size. Requires com.unity.visualeffectgraph.",
    ...VFX_GRAPH_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      eventName: { type: "string" },
      attributeName: { type: "string" },
      attributeX: { type: "number" },
      attributeY: { type: "number" },
      attributeZ: { type: "number" },
      attributeSize: { type: "number" }
    }, ["target", "eventName"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.event",
    examples: [
      { description: "Send an event with a position attribute", argv: ["tools", "run", "vfx.event", "--input", "{\"target\":\"VFXFire\",\"eventName\":\"Explode\",\"attributeName\":\"position\",\"attributeX\":1,\"attributeY\":2,\"attributeZ\":3}"] }
    ]
  },
  {
    name: "vfx.template",
    summary: "Create a Visual Effect Graph asset from a built-in template.",
    description: "Create a Visual Effect Graph asset from a built-in VFX Graph template. Requires com.unity.visualeffectgraph.",
    ...VFX_GRAPH_COMMON,
    inputSchema: objSchema({ path: { type: "string" }, template: { type: "string" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.template",
    examples: [
      { description: "Create a graph from an AR template", argv: ["tools", "run", "vfx.template", "--input", "{\"path\":\"Assets/VFX/Sparkle.vfx\",\"template\":\"AR/AugmentedRealityTemplate\"}"] }
    ]
  },
  {
    name: "vfx.particle-create",
    summary: "Create a ParticleSystem on a new or existing GameObject.",
    description: "Create a Unity built-in ParticleSystem on a new or existing GameObject. This does not require the Visual Effect Graph package.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      name: { type: "string" },
      duration: { type: "number" },
      startLifetime: { type: "number" },
      startSpeed: { type: "number" },
      startSize: { type: "number" },
      startColor: { type: "string", description: "#RRGGBB/#RRGGBBAA or r,g,b[,a]." },
      maxParticles: { type: "integer" },
      loop: { type: "boolean" }
    }),
    destructiveHint: false,
    cliCommand: "tools run vfx.particle-create"
  },
  {
    name: "vfx.particle-info",
    summary: "Get ParticleSystem state and module settings.",
    description: "Get ParticleSystem state, particle count, main settings, and summaries for emission, shape, color, size, rotation, velocity, noise, collision, sub-emitters and trails.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.particle-info"
  },
  {
    name: "vfx.particle-main",
    summary: "Set ParticleSystem main module properties.",
    description: "Set ParticleSystem main module properties: duration, lifetime, speed, size, color, rotation, gravity, max particles, loop, playOnAwake, simulation space, and scaling mode.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      duration: { type: "number" },
      startLifetime: { type: "number" },
      startSpeed: { type: "number" },
      startSize: { type: "number" },
      startColor: { type: "string" },
      startRotation: { type: "number", description: "Degrees; Unity stores radians internally." },
      gravityModifier: { type: "number" },
      maxParticles: { type: "integer" },
      loop: { type: "boolean" },
      playOnAwake: { type: "boolean" },
      simulationSpace: { type: "string", enum: ["Local", "World", "Custom"] },
      scalingMode: { type: "string", enum: ["Hierarchy", "Local", "Shape"] }
    }, ["target"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.particle-main"
  },
  {
    name: "vfx.particle-emission",
    summary: "Set ParticleSystem emission module properties.",
    description: "Set emission enabled state, rate over time/distance, and simple burst count. For detailed burst editing use object/component patch tools on the ParticleSystem when needed.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      enabled: { type: "boolean" },
      rateOverTime: { type: "number" },
      rateOverDistance: { type: "number" },
      burstCount: { type: "integer" }
    }, ["target"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.particle-emission"
  },
  {
    name: "vfx.particle-shape",
    summary: "Set ParticleSystem shape module properties.",
    description: "Set shape enabled state, shape type, radius, angle, arc, length, box dimensions, and random direction.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      enabled: { type: "boolean" },
      shapeType: { type: "string" },
      radius: { type: "number" },
      angle: { type: "number" },
      arc: { type: "number" },
      length: { type: "number" },
      boxX: { type: "number" },
      boxY: { type: "number" },
      boxZ: { type: "number" },
      randomDirection: { type: "boolean" }
    }, ["target"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.particle-shape"
  },
  {
    name: "vfx.particle-play",
    summary: "Play, stop, pause, or clear a ParticleSystem.",
    description: "Control a Unity built-in ParticleSystem: play, stop, pause, or clear, with optional child propagation and stop behavior.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      action: { type: "string", enum: ["play", "stop", "pause", "clear"] },
      withChildren: { type: "boolean" },
      stopBehavior: { type: "string", enum: ["StopEmitting", "StopEmittingAndClear"] }
    }, ["target"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.particle-play"
  },
  {
    name: "vfx.particle-module",
    summary: "Configure deep ParticleSystem modules.",
    description: "Enable and configure ParticleSystem modules: color/size/rotation/velocity/force over lifetime, noise, collision, sub-emitters, texture animation, lights, trails, inherit velocity, and limit velocity. Pass module plus only the fields to change.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      module: { type: "string", description: "color, size, rotation, velocity, force, noise, collision, subEmitter, textureAnimation, lights, trails, inheritVelocity, or limitVelocity." },
      enabled: { type: "boolean" },
      color: { type: "string" },
      size: { type: "number" },
      rotation: { type: "number", description: "Degrees per second for rotation over lifetime." },
      x: { type: "number" },
      y: { type: "number" },
      z: { type: "number" },
      space: { type: "string" },
      speedModifier: { type: "number" },
      strength: { type: "number" },
      frequency: { type: "number" },
      scrollSpeed: { type: "number" },
      octaveCount: { type: "integer" },
      quality: { type: "string" },
      collisionType: { type: "string" },
      collisionMode: { type: "string" },
      dampen: { type: "number" },
      bounce: { type: "number" },
      lifetimeLoss: { type: "number" },
      minKillSpeed: { type: "number" },
      maxKillSpeed: { type: "number" },
      radiusScale: { type: "number" },
      ratio: { type: "number", description: "Particle trail ratio for the trails module." },
      lifetime: { type: "number", description: "Particle trail lifetime multiplier for the trails module." },
      width: { type: "number", description: "Particle trail width multiplier for the trails module." },
      subEmitterAction: { type: "string", enum: ["add", "remove", "clear"] },
      subEmitterTarget: { type: "string" },
      subEmitterType: { type: "string" },
      subEmitterProperties: { type: "string" }
    }, ["target", "module"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.particle-module",
    examples: [
      { description: "Enable noise with strength and frequency", argv: ["tools", "run", "vfx.particle-module", "--input", "{\"target\":\"Dust\",\"module\":\"noise\",\"enabled\":true,\"strength\":0.4,\"frequency\":0.8}"] },
      { description: "Set velocity over lifetime", argv: ["tools", "run", "vfx.particle-module", "--input", "{\"target\":\"Dust\",\"module\":\"velocity\",\"enabled\":true,\"x\":0,\"y\":1,\"z\":0,\"space\":\"World\"}"] }
    ]
  },
  {
    name: "vfx.line-create",
    summary: "Create or configure a LineRenderer.",
    description: "Create a LineRenderer on a new or existing GameObject and configure width, color, material, width curve, color gradient, alignment, texture mode, loop, and world-space behavior.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      name: { type: "string" },
      startWidth: { type: "number" },
      endWidth: { type: "number" },
      widthMultiplier: { type: "number" },
      widthCurve: { type: ["string", "array"], items: curveKeySchema, description: "Array of {time,value} keys or string '0:1,1:0.5'." },
      startColor: { type: "string" },
      endColor: { type: "string" },
      colorGradient: { type: ["string", "array"], items: gradientKeySchema, description: "Array of {time,color} keys or string '0:#FFFFFF,1:#000000'." },
      material: { type: "string", description: "Assets/*.mat path. Use null/none to clear sharedMaterial." },
      positionCount: { type: "integer" },
      positions: { type: ["string", "array"], items: vector3Schema },
      loop: { type: "boolean" },
      useWorldSpace: { type: "boolean" },
      alignment: { type: "string", enum: ["View", "TransformZ"] },
      textureMode: { type: "string" },
      numCornerVertices: { type: "integer" },
      numCapVertices: { type: "integer" }
    }),
    destructiveHint: false,
    cliCommand: "tools run vfx.line-create",
    examples: [
      { description: "Create a line with material and width curve", argv: ["tools", "run", "vfx.line-create", "--input", "{\"name\":\"AimLine\",\"material\":\"Assets/UnityCliAlignmentFixtures/Line.mat\",\"widthCurve\":[{\"time\":0,\"value\":0.1},{\"time\":1,\"value\":0.02}],\"alignment\":\"View\"}"] }
    ]
  },
  {
    name: "vfx.line-positions",
    summary: "Set LineRenderer positions.",
    description: "Set all positions or a single indexed position on a LineRenderer. Prefer structured positions:[{x,y,z}] in --input for reliable quoting.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      positions: { type: ["string", "array"], items: vector3Schema },
      index: { type: "integer" },
      position: { type: ["string", "object"], properties: vector3Properties }
    }, ["target"]),
    destructiveHint: false,
    cliCommand: "tools run vfx.line-positions"
  },
  {
    name: "vfx.trail",
    summary: "Create or configure a TrailRenderer.",
    description: "Create or modify a TrailRenderer: time, width, width curve, color gradient, material, alignment, texture mode, vertex distance, emitting, and autodestruct.",
    ...BUILTIN_VFX_COMMON,
    inputSchema: objSchema({
      target: { type: "string" },
      name: { type: "string" },
      create: { type: "boolean" },
      time: { type: "number" },
      startWidth: { type: "number" },
      endWidth: { type: "number" },
      widthMultiplier: { type: "number" },
      widthCurve: { type: ["string", "array"], items: curveKeySchema },
      startColor: { type: "string" },
      endColor: { type: "string" },
      colorGradient: { type: ["string", "array"], items: gradientKeySchema },
      material: { type: "string", description: "Assets/*.mat path. Use null/none to clear sharedMaterial." },
      minVertexDistance: { type: "number" },
      autodestruct: { type: "boolean" },
      emitting: { type: "boolean" },
      alignment: { type: "string", enum: ["View", "TransformZ"] },
      textureMode: { type: "string" },
      numCornerVertices: { type: "integer" },
      numCapVertices: { type: "integer" }
    }),
    destructiveHint: false,
    cliCommand: "tools run vfx.trail",
    examples: [
      { description: "Create and configure a trail", argv: ["tools", "run", "vfx.trail", "--input", "{\"name\":\"DashTrail\",\"create\":true,\"material\":\"Assets/UnityCliAlignmentFixtures/Trail.mat\",\"colorGradient\":[{\"time\":0,\"color\":\"#FFFFFFFF\"},{\"time\":1,\"color\":\"#00FFFFFF\"}],\"widthCurve\":[{\"time\":0,\"value\":0.3},{\"time\":1,\"value\":0}]}"] }
    ]
  }
];
