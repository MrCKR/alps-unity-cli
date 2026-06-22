import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const physicsBridgeSpecs: BridgeCommandSpec[] = [
  { group: "physics", command: "settings", bridgeCommand: "physics.settings", buildArgs: physicsArgs },
  { group: "physics", command: "collision-matrix", bridgeCommand: "physics.collision-matrix", buildArgs: physicsArgs },
  { group: "physics", command: "material", bridgeCommand: "physics.material", buildArgs: physicsArgs },
  { group: "physics", command: "joint", bridgeCommand: "physics.joint", buildArgs: physicsArgs },
  { group: "physics", command: "force", bridgeCommand: "physics.force", buildArgs: physicsArgs },
  { group: "physics", command: "overlap", bridgeCommand: "physics.overlap", buildArgs: physicsArgs },
  { group: "physics", command: "rigidbody", bridgeCommand: "physics.rigidbody", buildArgs: physicsArgs },
  { group: "physics", command: "raycast", bridgeCommand: "physics.raycast", buildArgs: physicsArgs },
  { group: "physics", command: "simulation", bridgeCommand: "physics.simulation", buildArgs: physicsArgs }
];

function physicsArgs({ command, flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "physics.raycast" || command === "physics.overlap") {
    return compactArgs({
      target: stringFlag(flags, "target"),
      shape: stringFlag(flags, "shape"),
      radius: floatFlag(flags, "radius"),
      sizeX: floatFlag(flags, "size-x"),
      sizeY: floatFlag(flags, "size-y"),
      sizeZ: floatFlag(flags, "size-z"),
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
  if (command === "physics.collision-matrix") {
    return compactArgs({ layerA: stringFlag(flags, "layer-a"), layerB: stringFlag(flags, "layer-b"), ignore: boolFlag(flags, "ignore") });
  }
  if (command === "physics.material") {
    return compactArgs({ path: stringFlag(flags, "path"), dynamicFriction: floatFlag(flags, "dynamic-friction"), staticFriction: floatFlag(flags, "static-friction"), bounciness: floatFlag(flags, "bounciness"), overwrite: boolFlag(flags, "overwrite") });
  }
  if (command === "physics.joint") {
    return compactArgs({ target: stringFlag(flags, "target"), type: stringFlag(flags, "type"), connectedBody: stringFlag(flags, "connected-body"), anchor: stringFlag(flags, "anchor") });
  }
  if (command === "physics.force") {
    return compactArgs({ target: stringFlag(flags, "target"), x: floatFlag(flags, "x"), y: floatFlag(flags, "y"), z: floatFlag(flags, "z"), mode: stringFlag(flags, "mode") });
  }
  if (command === "physics.rigidbody") {
    return compactArgs({ target: stringFlag(flags, "target"), mass: floatFlag(flags, "mass"), drag: floatFlag(flags, "drag"), angularDrag: floatFlag(flags, "angular-drag"), useGravity: boolFlag(flags, "use-gravity"), isKinematic: boolFlag(flags, "is-kinematic"), validate: boolFlag(flags, "validate") });
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
