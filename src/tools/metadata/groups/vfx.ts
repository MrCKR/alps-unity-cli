import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const VFX_TOOLS: ToolMetadata[] = [
  {
    name: "vfx.graph",
    title: "VFX Graph",
    description: "Get info on a Visual Effect graph asset, optionally create one. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({ path: { type: "string" }, action: { type: "string", enum: ["info", "create"] }, create: { type: "boolean" }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx graph",
    docsTopic: "vfx",
    requiresPackages: ["com.unity.visualeffectgraph"]
  },
  {
    name: "vfx.effect",
    title: "VFX Effect",
    description: "Manage a Visual Effect instance on a GameObject. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({ target: { type: "string" }, graph: { type: "string" }, add: { type: "boolean" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx effect",
    docsTopic: "vfx",
    requiresPackages: ["com.unity.visualeffectgraph"]
  },
  // ===== ParticleSystem =====
  {
    name: "vfx.particle-create",
    title: "Create ParticleSystem",
    description: "Create a ParticleSystem on a new or existing GameObject with optional initial settings.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      name: { type: "string" },
      duration: { type: "number" },
      startLifetime: { type: "number" },
      startSpeed: { type: "number" },
      maxParticles: { type: "integer" },
      loop: { type: "boolean" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx particle-create",
    docsTopic: "vfx"
  },
  {
    name: "vfx.particle-info",
    title: "ParticleSystem Info",
    description: "Get ParticleSystem state, particle count, and main module settings.",
    group: "vfx",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx particle-info",
    docsTopic: "vfx"
  },
  {
    name: "vfx.particle-main",
    title: "Set ParticleSystem Main Module",
    description: "Set main module properties: duration, lifetime, speed, size, color, rotation, gravity, max particles, loop, simulation space, scaling mode.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      duration: { type: "number" },
      startLifetime: { type: "number" },
      startSpeed: { type: "number" },
      startSize: { type: "number" },
      startColor: { type: "string" },
      startRotation: { type: "number" },
      gravityModifier: { type: "number" },
      maxParticles: { type: "integer" },
      loop: { type: "boolean" },
      playOnAwake: { type: "boolean" },
      simulationSpace: { type: "string" },
      scalingMode: { type: "string" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx particle-main",
    docsTopic: "vfx"
  },
  {
    name: "vfx.particle-emission",
    title: "Set ParticleSystem Emission",
    description: "Set emission module: enabled, rate over time/distance, burst count.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      enabled: { type: "boolean" },
      rateOverTime: { type: "number" },
      rateOverDistance: { type: "number" },
      burstCount: { type: "integer" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx particle-emission",
    docsTopic: "vfx"
  },
  {
    name: "vfx.particle-shape",
    title: "Set ParticleSystem Shape",
    description: "Set shape module: type, radius, angle, arc, length, box dimensions, random direction.",
    group: "vfx",
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
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx particle-shape",
    docsTopic: "vfx"
  },
  {
    name: "vfx.particle-play",
    title: "ParticleSystem Play Control",
    description: "Play, stop, pause, or clear a ParticleSystem.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      action: { type: "string", enum: ["play", "stop", "pause", "clear"] },
      withChildren: { type: "boolean" },
      stopBehavior: { type: "string" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx particle-play",
    docsTopic: "vfx"
  },
  {
    name: "vfx.particle-module",
    title: "ParticleSystem Module Toggle",
    description: "Enable or disable ParticleSystem modules: color/size/rotation/velocity/force over lifetime, noise, collision, sub-emitters, texture animation, lights, trails, inherit velocity, limit velocity.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      module: { type: "string" },
      enabled: { type: "boolean" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx particle-module",
    docsTopic: "vfx"
  },
  // ===== Line Renderer =====
  {
    name: "vfx.line-create",
    title: "Create Line Renderer",
    description: "Create a LineRenderer on a new or existing GameObject with optional width, color, and position settings.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      name: { type: "string" },
      startWidth: { type: "number" },
      endWidth: { type: "number" },
      startColor: { type: "string" },
      endColor: { type: "string" },
      positionCount: { type: "integer" },
      loop: { type: "boolean" },
      useWorldSpace: { type: "boolean" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx line-create",
    docsTopic: "vfx"
  },
  {
    name: "vfx.line-positions",
    title: "Set Line Renderer Positions",
    description: "Set all positions or a single position by index on a LineRenderer.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      positions: { type: "string" },
      index: { type: "integer" },
      position: { type: "string" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx line-positions",
    docsTopic: "vfx"
  },
  // ===== Trail Renderer =====
  {
    name: "vfx.trail",
    title: "Trail Renderer",
    description: "Create or modify a TrailRenderer: time, width, vertex distance, emitting, autodestruct.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      name: { type: "string" },
      create: { type: "boolean" },
      time: { type: "number" },
      startWidth: { type: "number" },
      endWidth: { type: "number" },
      minVertexDistance: { type: "number" },
      autodestruct: { type: "boolean" },
      emitting: { type: "boolean" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx trail",
    docsTopic: "vfx"
  }
];
