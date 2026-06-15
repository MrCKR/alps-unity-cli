import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const physicsBridgeSpecs: BridgeCommandSpec[] = [
  { group: "physics", command: "settings", bridgeCommand: "physics.settings", buildArgs: physicsArgs },
  { group: "physics", command: "raycast", bridgeCommand: "physics.raycast", buildArgs: physicsArgs },
  { group: "physics", command: "simulation", bridgeCommand: "physics.simulation", buildArgs: physicsArgs }
];

function physicsArgs({ command, flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "physics.raycast") {
    return compactArgs({
      mode: stringFlag(flags, "mode"),
      originX: floatFlag(flags, "origin-x") ?? floatFlag(flags, "x"),
      originY: floatFlag(flags, "origin-y") ?? floatFlag(flags, "y"),
      originZ: floatFlag(flags, "origin-z") ?? floatFlag(flags, "z"),
      directionX: floatFlag(flags, "direction-x") ?? floatFlag(flags, "dx"),
      directionY: floatFlag(flags, "direction-y") ?? floatFlag(flags, "dy"),
      directionZ: floatFlag(flags, "direction-z") ?? floatFlag(flags, "dz"),
      distance: floatFlag(flags, "distance"),
      layerMask: numberFlag(flags, "layer-mask"),
      all: boolFlag(flags, "all")
    });
  }
  if (command === "physics.simulation") {
    return compactArgs({ mode: stringFlag(flags, "mode"), steps: numberFlag(flags, "steps"), deltaTime: floatFlag(flags, "delta-time"), simulate: boolFlag(flags, "simulate") });
  }
  return compactArgs({
    mode: stringFlag(flags, "mode"),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value"),
    gravityX: floatFlag(flags, "gravity-x"),
    gravityY: floatFlag(flags, "gravity-y"),
    gravityZ: floatFlag(flags, "gravity-z"),
    gravity2dX: floatFlag(flags, "gravity2d-x"),
    gravity2dY: floatFlag(flags, "gravity2d-y"),
    defaultContactOffset: floatFlag(flags, "default-contact-offset"),
    bounceThreshold: floatFlag(flags, "bounce-threshold"),
    defaultSolverIterations: numberFlag(flags, "default-solver-iterations"),
    defaultSolverVelocityIterations: numberFlag(flags, "default-solver-velocity-iterations"),
    queriesHitTriggers: boolFlag(flags, "queries-hit-triggers"),
    queriesHitBackfaces: boolFlag(flags, "queries-hit-backfaces"),
    queriesStartInColliders: boolFlag(flags, "queries-start-in-colliders"),
    autoSimulation: boolFlag(flags, "auto-simulation")
  });
}
