import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const PHYSICS_TOOLS: ToolMetadata[] = [
  {
    name: "physics.settings",
    title: "Physics Settings",
    description: "Get or set physics gravity, mode and other global settings.",
    group: "physics",
    inputSchema: objSchema({
      mode: { type: "string", enum: ["3d", "2d"] },
      gravityX: { type: "number" },
      gravityY: { type: "number" },
      gravityZ: { type: "number" },
      gravity2dX: { type: "number" },
      gravity2dY: { type: "number" },
      defaultContactOffset: { type: "number" },
      bounceThreshold: { type: "number" },
      defaultSolverIterations: { type: "integer" },
      defaultSolverVelocityIterations: { type: "integer" },
      queriesHitTriggers: { type: "boolean" },
      queriesHitBackfaces: { type: "boolean" },
      queriesStartInColliders: { type: "boolean" },
      autoSimulation: { type: "boolean" },
      property: { type: "string" },
      value: { type: "string" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "physics settings",
    docsTopic: "physics"
  },
  {
    name: "physics.raycast",
    title: "Physics Raycast",
    description: "Cast a ray and report the first hit.",
    group: "physics",
    inputSchema: objSchema({ originX: { type: "number" }, originY: { type: "number" }, originZ: { type: "number" }, directionX: { type: "number" }, directionY: { type: "number" }, directionZ: { type: "number" }, distance: { type: "number" }, mode: { type: "string", enum: ["3d", "2d"] }, layerMask: { type: "integer" }, all: { type: "boolean" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "physics raycast",
    docsTopic: "physics"
  },
  {
    name: "physics.simulation",
    title: "Physics Simulation",
    description: "Step the physics simulation forward.",
    group: "physics",
    inputSchema: objSchema({ steps: { type: "integer" }, deltaTime: { type: "number" }, simulate: { type: "boolean" }, mode: { type: "string", enum: ["3d", "2d"] } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "physics simulation",
    docsTopic: "physics"
  }
];
