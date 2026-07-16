import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const physicsBridgeSpecs: BridgeCommandSpec[] = [
  { group: "physics", command: "settings", bridgeCommand: "project-tool.run", toolName: "physics.settings", buildArgs: physicsArgs, destructiveHint: true },
  { group: "physics", command: "collision-matrix", bridgeCommand: "project-tool.run", toolName: "physics.collision-matrix", buildArgs: physicsArgs, destructiveHint: true },
  { group: "physics", command: "material", bridgeCommand: "project-tool.run", toolName: "physics.material", buildArgs: physicsArgs, destructiveHint: true },
  { group: "physics", command: "assign-material", bridgeCommand: "project-tool.run", toolName: "physics.assign-material", buildArgs: physicsArgs, supportsDryRun: true },
  { group: "physics", command: "joint-add", bridgeCommand: "project-tool.run", toolName: "physics.joint.add", buildArgs: physicsArgs, supportsDryRun: true },
  { group: "physics", command: "joint-remove", bridgeCommand: "project-tool.run", toolName: "physics.joint.remove", buildArgs: physicsArgs, supportsDryRun: true },
  { group: "physics", command: "force", bridgeCommand: "project-tool.run", toolName: "physics.force", buildArgs: physicsArgs },
  { group: "physics", command: "overlap", bridgeCommand: "project-tool.run", toolName: "physics.overlap", buildArgs: physicsArgs },
  { group: "physics", command: "rigidbody", bridgeCommand: "project-tool.run", toolName: "physics.rigidbody", buildArgs: physicsArgs },
  { group: "physics", command: "raycast", bridgeCommand: "project-tool.run", toolName: "physics.raycast", buildArgs: physicsArgs },
  { group: "physics", command: "raycast-all", bridgeCommand: "project-tool.run", toolName: "physics.raycast-all", buildArgs: physicsArgs },
  { group: "physics", command: "linecast", bridgeCommand: "project-tool.run", toolName: "physics.linecast", buildArgs: physicsArgs },
  { group: "physics", command: "shapecast", bridgeCommand: "project-tool.run", toolName: "physics.shapecast", buildArgs: physicsArgs },
  { group: "physics", command: "validate", bridgeCommand: "project-tool.run", toolName: "physics.validate", buildArgs: physicsValidateArgs },
  { group: "physics", command: "simulation", bridgeCommand: "project-tool.run", toolName: "physics.simulation", buildArgs: physicsArgs }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段
function structuredInput(flags: BridgeFlags): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function stringField(flags: BridgeFlags, key: string): string | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "string" ? value : undefined;
}

function numberField(flags: BridgeFlags, key: string): number | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "number" ? value : undefined;
}

function boolField(flags: BridgeFlags, key: string): boolean | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "boolean" ? value : undefined;
}

function physicsArgs({ command, flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "physics.raycast" || command === "physics.overlap" || command === "physics.raycast-all" || command === "physics.shapecast") {
    return compactArgs({
      target: stringFlag(flags, "target") ?? stringField(flags, "target"),
      shape: stringFlag(flags, "shape") ?? stringField(flags, "shape"),
      radius: floatFlag(flags, "radius") ?? numberField(flags, "radius"),
      sizeX: floatFlag(flags, "size-x") ?? numberField(flags, "sizeX"),
      sizeY: floatFlag(flags, "size-y") ?? numberField(flags, "sizeY"),
      sizeZ: floatFlag(flags, "size-z") ?? numberField(flags, "sizeZ"),
      halfExtentX: floatFlag(flags, "half-extent-x") ?? numberField(flags, "halfExtentX"),
      halfExtentY: floatFlag(flags, "half-extent-y") ?? numberField(flags, "halfExtentY"),
      halfExtentZ: floatFlag(flags, "half-extent-z") ?? numberField(flags, "halfExtentZ"),
      height: floatFlag(flags, "height") ?? numberField(flags, "height"),
      capsuleDirection: stringFlag(flags, "capsule-direction") ?? numberFlag(flags, "capsule-direction") ?? stringField(flags, "capsuleDirection") ?? numberField(flags, "capsuleDirection"),
      angle: floatFlag(flags, "angle") ?? numberField(flags, "angle"),
      point1X: floatFlag(flags, "point1-x") ?? numberField(flags, "point1X"),
      point1Y: floatFlag(flags, "point1-y") ?? numberField(flags, "point1Y"),
      point1Z: floatFlag(flags, "point1-z") ?? numberField(flags, "point1Z"),
      point2X: floatFlag(flags, "point2-x") ?? numberField(flags, "point2X"),
      point2Y: floatFlag(flags, "point2-y") ?? numberField(flags, "point2Y"),
      point2Z: floatFlag(flags, "point2-z") ?? numberField(flags, "point2Z"),
      mode: stringFlag(flags, "mode") ?? stringField(flags, "mode"),
      originX: floatFlag(flags, "origin-x") ?? floatFlag(flags, "x") ?? numberField(flags, "originX"),
      originY: floatFlag(flags, "origin-y") ?? floatFlag(flags, "y") ?? numberField(flags, "originY"),
      originZ: floatFlag(flags, "origin-z") ?? floatFlag(flags, "z") ?? numberField(flags, "originZ"),
      directionX: floatFlag(flags, "direction-x") ?? floatFlag(flags, "dx") ?? numberField(flags, "directionX"),
      directionY: floatFlag(flags, "direction-y") ?? floatFlag(flags, "dy") ?? numberField(flags, "directionY"),
      directionZ: floatFlag(flags, "direction-z") ?? floatFlag(flags, "dz") ?? numberField(flags, "directionZ"),
      distance: floatFlag(flags, "distance") ?? numberField(flags, "distance"),
      layerMask: numberFlag(flags, "layer-mask") ?? numberField(flags, "layerMask"),
      all: boolFlag(flags, "all") ?? boolField(flags, "all")
    });
  }
  if (command === "physics.linecast") {
    return compactArgs({
      mode: stringFlag(flags, "mode") ?? stringField(flags, "mode"),
      startX: floatFlag(flags, "start-x") ?? numberField(flags, "startX"),
      startY: floatFlag(flags, "start-y") ?? numberField(flags, "startY"),
      startZ: floatFlag(flags, "start-z") ?? numberField(flags, "startZ"),
      endX: floatFlag(flags, "end-x") ?? numberField(flags, "endX"),
      endY: floatFlag(flags, "end-y") ?? numberField(flags, "endY"),
      endZ: floatFlag(flags, "end-z") ?? numberField(flags, "endZ"),
      layerMask: numberFlag(flags, "layer-mask") ?? numberField(flags, "layerMask")
    });
  }
  if (command === "physics.simulation") {
    return compactArgs({ mode: stringFlag(flags, "mode") ?? stringField(flags, "mode"), steps: numberFlag(flags, "steps") ?? numberField(flags, "steps"), deltaTime: floatFlag(flags, "delta-time") ?? numberField(flags, "deltaTime"), simulate: boolFlag(flags, "simulate") ?? boolField(flags, "simulate") });
  }
  if (command === "physics.collision-matrix") {
    return compactArgs({ layerA: stringFlag(flags, "layer-a") ?? stringField(flags, "layerA"), layerB: stringFlag(flags, "layer-b") ?? stringField(flags, "layerB"), ignore: boolFlag(flags, "ignore") ?? boolField(flags, "ignore") });
  }
  if (command === "physics.material") {
    return compactArgs({ path: stringFlag(flags, "path") ?? stringField(flags, "path"), dynamicFriction: floatFlag(flags, "dynamic-friction") ?? numberField(flags, "dynamicFriction"), staticFriction: floatFlag(flags, "static-friction") ?? numberField(flags, "staticFriction"), bounciness: floatFlag(flags, "bounciness") ?? numberField(flags, "bounciness"), overwrite: boolFlag(flags, "overwrite") ?? boolField(flags, "overwrite") });
  }
  if (command === "physics.assign-material") {
    return compactArgs({ target: stringFlag(flags, "target") ?? stringField(flags, "target"), materialPath: stringFlag(flags, "material-path") ?? stringFlag(flags, "path") ?? stringField(flags, "materialPath") ?? stringField(flags, "path"), path: stringFlag(flags, "path") ?? stringField(flags, "path"), collider: stringFlag(flags, "collider") ?? stringField(flags, "collider"), colliderIndex: numberFlag(flags, "collider-index") ?? numberField(flags, "colliderIndex") });
  }
  if (command === "physics.joint.add") {
    return compactArgs({ target: stringFlag(flags, "target") ?? stringField(flags, "target"), type: stringFlag(flags, "type") ?? stringField(flags, "type"), connectedBody: stringFlag(flags, "connected-body") ?? stringField(flags, "connectedBody") });
  }
  if (command === "physics.joint.remove") {
    return compactArgs({ target: stringFlag(flags, "target") ?? stringField(flags, "target"), type: stringFlag(flags, "type") ?? stringField(flags, "type"), componentIndex: numberFlag(flags, "component-index") ?? numberField(flags, "componentIndex") });
  }
  if (command === "physics.force") {
    return compactArgs({ target: stringFlag(flags, "target") ?? stringField(flags, "target"), x: floatFlag(flags, "x") ?? numberField(flags, "x"), y: floatFlag(flags, "y") ?? numberField(flags, "y"), z: floatFlag(flags, "z") ?? numberField(flags, "z"), mode: stringFlag(flags, "mode") ?? stringField(flags, "mode"), forceType: stringFlag(flags, "force-type") ?? stringFlag(flags, "forceType") ?? stringField(flags, "forceType"), torqueX: floatFlag(flags, "torque-x") ?? numberField(flags, "torqueX"), torqueY: floatFlag(flags, "torque-y") ?? numberField(flags, "torqueY"), torqueZ: floatFlag(flags, "torque-z") ?? numberField(flags, "torqueZ"), explosionX: floatFlag(flags, "explosion-x") ?? numberField(flags, "explosionX"), explosionY: floatFlag(flags, "explosion-y") ?? numberField(flags, "explosionY"), explosionZ: floatFlag(flags, "explosion-z") ?? numberField(flags, "explosionZ"), explosionRadius: floatFlag(flags, "explosion-radius") ?? numberField(flags, "explosionRadius"), explosionForce: floatFlag(flags, "explosion-force") ?? numberField(flags, "explosionForce"), upwardsModifier: floatFlag(flags, "upwards-modifier") ?? numberField(flags, "upwardsModifier") });
  }
  if (command === "physics.rigidbody") {
    return compactArgs({ target: stringFlag(flags, "target") ?? stringField(flags, "target"), mass: floatFlag(flags, "mass") ?? numberField(flags, "mass"), drag: floatFlag(flags, "drag") ?? numberField(flags, "drag"), angularDrag: floatFlag(flags, "angular-drag") ?? numberField(flags, "angularDrag"), useGravity: boolFlag(flags, "use-gravity") ?? boolField(flags, "useGravity"), isKinematic: boolFlag(flags, "is-kinematic") ?? boolField(flags, "isKinematic"), validate: boolFlag(flags, "validate") ?? boolField(flags, "validate") });
  }
  return compactArgs({
    mode: stringFlag(flags, "mode") ?? stringField(flags, "mode"),
    property: stringFlag(flags, "property") ?? stringField(flags, "property"),
    value: stringFlag(flags, "value") ?? stringField(flags, "value"),
    gravityX: floatFlag(flags, "gravity-x") ?? numberField(flags, "gravityX"),
    gravityY: floatFlag(flags, "gravity-y") ?? numberField(flags, "gravityY"),
    gravityZ: floatFlag(flags, "gravity-z") ?? numberField(flags, "gravityZ"),
    gravity2dX: floatFlag(flags, "gravity2d-x") ?? numberField(flags, "gravity2dX"),
    gravity2dY: floatFlag(flags, "gravity2d-y") ?? numberField(flags, "gravity2dY"),
    defaultContactOffset: floatFlag(flags, "default-contact-offset") ?? numberField(flags, "defaultContactOffset"),
    bounceThreshold: floatFlag(flags, "bounce-threshold") ?? numberField(flags, "bounceThreshold"),
    defaultSolverIterations: numberFlag(flags, "default-solver-iterations") ?? numberField(flags, "defaultSolverIterations"),
    defaultSolverVelocityIterations: numberFlag(flags, "default-solver-velocity-iterations") ?? numberField(flags, "defaultSolverVelocityIterations"),
    queriesHitTriggers: boolFlag(flags, "queries-hit-triggers") ?? boolField(flags, "queriesHitTriggers"),
    queriesHitBackfaces: boolFlag(flags, "queries-hit-backfaces") ?? boolField(flags, "queriesHitBackfaces"),
    queriesStartInColliders: boolFlag(flags, "queries-start-in-colliders") ?? boolField(flags, "queriesStartInColliders"),
    autoSimulation: boolFlag(flags, "auto-simulation") ?? boolField(flags, "autoSimulation")
  });
}

function physicsValidateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"), path: stringFlag(flags, "path") ?? stringField(flags, "path") });
}
