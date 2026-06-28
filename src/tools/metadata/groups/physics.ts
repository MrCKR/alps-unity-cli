import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const PHYSICS_TOOLS: ToolMetadata[] = [
  {
    name: "physics.settings",
    summary: "Read or set physics gravity (3d/2d), default contact offset, bounce threshold, solver iterations and query flags.",
    description: "Read or set physics gravity (3d/2d), default contact offset, bounce threshold, solver iterations and query flags. With no setting fields it is a read; with any setting field it writes ProjectSettings/DynamicsManager. Use mode 3d/2d to target the right manager.",
    group: "physics",
    inputSchema: objSchema({
      mode: { type: "string", enum: ["3d", "2d"], description: "Target 3D or 2D physics." },
      gravityX: { type: "number" }, gravityY: { type: "number" }, gravityZ: { type: "number" },
      gravity2dX: { type: "number" }, gravity2dY: { type: "number" },
      defaultContactOffset: { type: "number" }, bounceThreshold: { type: "number" },
      defaultSolverIterations: { type: "integer" }, defaultSolverVelocityIterations: { type: "integer" },
      queriesHitTriggers: { type: "boolean" }, queriesHitBackfaces: { type: "boolean" }, queriesStartInColliders: { type: "boolean" },
      autoSimulation: { type: "boolean" }, property: { type: "string" }, value: { type: "string" }
    }),
    destructiveHint: false,
    sideEffects: ["project-settings-write"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.settings",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.set_settings",
    examples: [
      { description: "Read current physics settings", argv: ["tools", "run", "physics.settings", "--input", "{\"mode\":\"3d\"}"] }
    ]
  },
  {
    name: "physics.collision-matrix",
    summary: "Read the full 32-layer collision matrix or read/update one layer pair by layerA/layerB.",
    description: "Read the full 32-layer collision matrix when layerA/layerB are omitted, or read/update one layer pair by layerA/layerB. Setting ignore=true disables collision between the two layers. Pair updates write ProjectSettings/TagManager.",
    group: "physics",
    inputSchema: objSchema({ layerA: { type: "string", description: "First layer name or numeric index. Omit with layerB to read the full matrix." }, layerB: { type: "string", description: "Second layer name or numeric index. Omit with layerA to read the full matrix." }, ignore: { type: "boolean", description: "True to ignore/disable collision between layerA and layerB; false enables collision." } }),
    destructiveHint: false,
    sideEffects: ["project-settings-write"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.collision-matrix",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.collision_matrix",
    examples: [
      { description: "Read the full layer collision matrix", argv: ["tools", "run", "physics.collision-matrix", "--input", "{}"] },
      { description: "Read collision state between two layers", argv: ["tools", "run", "physics.collision-matrix", "--input", "{\"layerA\":\"Default\",\"layerB\":\"Ignore Raycast\"}"] }
    ]
  },
  {
    name: "physics.material",
    summary: "Create or update a PhysicMaterial asset with friction and bounciness settings at an Assets/*.physicMaterial path.",
    description: "Create or update a PhysicMaterial asset with friction and bounciness settings at an Assets/*.physicMaterial path.",
    group: "physics",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/*.physicMaterial path." }, dynamicFriction: { type: "number" }, staticFriction: { type: "number" }, bounciness: { type: "number" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.material",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.physics_material",
    examples: [
      { description: "Create a bouncy physic material", argv: ["tools", "run", "physics.material", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/_probe.physicMaterial\",\"bounciness\":0.8}"] }
    ]
  },
  {
    name: "physics.assign-material",
    summary: "Assign an existing PhysicMaterial asset to a 3D Collider on a GameObject without modifying the material asset.",
    description: "Assign an existing PhysicMaterial asset to a 3D Collider on a GameObject without modifying the material asset. target resolves by name, hierarchy path or instance id. materialPath points to an existing Assets/*.physicMaterial or .asset PhysicMaterial. collider/colliderIndex select among multiple Collider components.",
    group: "physics",
    inputSchema: objSchema({ target: { type: "string", description: "Target GameObject with a 3D Collider." }, materialPath: { type: "string", description: "Existing PhysicMaterial asset path under Assets/." }, path: { type: "string", description: "Alias of materialPath." }, collider: { type: "string", description: "Optional Collider type name, for example BoxCollider." }, colliderIndex: { type: "integer", description: "Index among matching Collider components. Defaults to 0." } }, ["target", "materialPath"]),
    destructiveHint: false,
    supportsDryRun: true,
    sideEffects: ["scene-modify"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.assign-material",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.assign_physics_material",
    examples: [
      { description: "Assign a PhysicMaterial to the first Collider on a target", argv: ["tools", "run", "physics.assign-material", "--input", "{\"target\":\"Ball\",\"materialPath\":\"Assets/Physics/Bouncy.physicMaterial\"}"] }
    ]
  },
  {
    name: "physics.joint",
    summary: "Add, configure or remove a Joint component (Fixed/Hinge/Spring/Configurable) on a GameObject.",
    description: "Add, configure or remove a Joint component (Fixed/Hinge/Spring/Configurable) on a GameObject. target resolves by name, hierarchy path or instance id. action defaults to add/configure; action=remove removes matching joints, or a single matching joint when componentIndex is provided.",
    group: "physics",
    inputSchema: objSchema({ target: { type: "string", description: "Target GameObject with Rigidbody." }, type: { type: "string", description: "Joint type: Fixed, HingeJoint, SpringJoint, ConfigurableJoint." }, action: { type: "string", enum: ["add", "configure", "remove"], description: "Operation to perform. Defaults to add/configure." }, connectedBody: { type: "string", description: "Connected Rigidbody target for add/configure." }, componentIndex: { type: "integer", description: "For action=remove, remove this matching joint index; omit to remove all matching joints." }, anchor: { type: "string", description: "Anchor reference." } }, ["target", "type"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.joint",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.joints",
    examples: [
      { description: "Add a FixedJoint to a Rigidbody", argv: ["tools", "run", "physics.joint", "--input", "{\"target\":\"PhysicsProbe\",\"type\":\"FixedJoint\"}"] },
      { description: "Remove all FixedJoint components from a target", argv: ["tools", "run", "physics.joint", "--input", "{\"target\":\"PhysicsProbe\",\"type\":\"FixedJoint\",\"action\":\"remove\"}"] }
    ]
  },
  {
    name: "physics.force",
    summary: "Apply a force, torque or explosion to a Rigidbody.",
    description: "Apply a force, torque or explosion to a Rigidbody. Meaningful in Play Mode with auto-simulation; in Edit Mode the velocity is set but no integration runs. forceType normal applies force [x,y,z] + optional torque; explosion applies at a position with radius.",
    group: "physics",
    inputSchema: objSchema({ target: { type: "string", description: "Target GameObject with Rigidbody." }, x: { type: "number" }, y: { type: "number" }, z: { type: "number" }, mode: { type: "string" }, forceType: { type: "string", enum: ["normal", "explosion"] }, torqueX: { type: "number" }, torqueY: { type: "number" }, torqueZ: { type: "number" }, explosionX: { type: "number" }, explosionY: { type: "number" }, explosionZ: { type: "number" }, explosionRadius: { type: "number" }, explosionForce: { type: "number" }, upwardsModifier: { type: "number" } }, ["target"]),
    destructiveHint: false,
    sideEffects: ["runtime-state"],
    riskLevel: "low",
    playModeSupport: "runtime-only",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.force",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.forces",
    examples: [
      { description: "Apply an upward force", argv: ["tools", "run", "physics.force", "--input", "{\"target\":\"PhysicsProbe\",\"x\":0,\"y\":10,\"z\":0}"] }
    ]
  },
  {
    name: "physics.overlap",
    summary: "Run a 3D sphere/box or 2D circle/box/capsule overlap query and return matching colliders.",
    description: "Run a 3D sphere/box or 2D circle/box/capsule overlap query at a position and return matching colliders. Read-only query. Use mode 2d for Physics2D; 2D ignores originZ.",
    group: "physics",
    inputSchema: objSchema({ mode: { type: "string", enum: ["3d", "2d"] }, shape: { type: "string", enum: ["sphere", "circle", "box", "capsule"] }, originX: { type: "number" }, originY: { type: "number" }, originZ: { type: "number" }, radius: { type: "number" }, sizeX: { type: "number" }, sizeY: { type: "number" }, sizeZ: { type: "number" }, height: { type: "number" }, angle: { type: "number" }, capsuleDirection: { type: "string", enum: ["vertical", "horizontal"] }, layerMask: { type: "integer" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.overlap",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.overlap_queries",
    examples: [
      { description: "Sphere overlap at origin", argv: ["tools", "run", "physics.overlap", "--input", "{\"shape\":\"sphere\",\"originX\":0,\"originY\":0,\"originZ\":0,\"radius\":5}"] }
    ]
  },
  {
    name: "physics.rigidbody",
    summary: "Read, validate or configure a Rigidbody's mass, drag, angularDrag, useGravity and isKinematic flags.",
    description: "Read, validate or configure a Rigidbody's mass, drag, angularDrag, useGravity and isKinematic flags. target resolves by name, hierarchy path or instance id. Pass validate=true to read current Rigidbody/Rigidbody2D state without adding or modifying components.",
    group: "physics",
    inputSchema: objSchema({ target: { type: "string", description: "Target GameObject with Rigidbody." }, mass: { type: "number" }, drag: { type: "number" }, angularDrag: { type: "number" }, useGravity: { type: "boolean" }, isKinematic: { type: "boolean" }, validate: { type: "boolean", description: "Only validate, do not change." } }, ["target"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.rigidbody",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.rigidbody",
    examples: [
      { description: "Read current Rigidbody state without modifying the target", argv: ["tools", "run", "physics.rigidbody", "--input", "{\"target\":\"PhysicsProbe\",\"validate\":true}"] },
      { description: "Configure a Rigidbody", argv: ["tools", "run", "physics.rigidbody", "--input", "{\"target\":\"PhysicsProbe\",\"mass\":2.0,\"useGravity\":true}"] }
    ]
  },
  {
    name: "physics.raycast",
    summary: "Cast a ray (3d/2d) from origin along direction and report the first hit (or all hits with all=true).",
    description: "Cast a ray (3d/2d) from origin along direction and report the first hit (or all hits with all=true). Returns hit point, normal, distance, collider and transform reference. Read-only query.",
    group: "physics",
    inputSchema: objSchema({ originX: { type: "number" }, originY: { type: "number" }, originZ: { type: "number" }, directionX: { type: "number" }, directionY: { type: "number" }, directionZ: { type: "number" }, distance: { type: "number" }, mode: { type: "string", enum: ["3d", "2d"] }, layerMask: { type: "integer" }, all: { type: "boolean" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.raycast",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.raycast",
    examples: [
      { description: "Raycast forward from origin", argv: ["tools", "run", "physics.raycast", "--input", "{\"originX\":0,\"originY\":0,\"originZ\":0,\"directionX\":0,\"directionY\":0,\"directionZ\":1,\"distance\":100}"] }
    ]
  },
  {
    name: "physics.simulation",
    summary: "Step the physics simulation forward by N steps of deltaTime.",
    description: "Step the physics simulation forward by N steps of deltaTime. Best used in Play Mode (runtime-only semantics); in Edit Mode it manually steps without rendering. Use mode 3d/2d.",
    group: "physics",
    inputSchema: objSchema({ steps: { type: "integer" }, deltaTime: { type: "number" }, simulate: { type: "boolean" }, mode: { type: "string", enum: ["3d", "2d"] } }),
    destructiveHint: false,
    sideEffects: ["runtime-state"],
    riskLevel: "low",
    playModeSupport: "runtime-only",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.simulation",
    docsTopic: "physics",
    equivalentUnityMcp: "manage_physics.simulate_step",
    examples: [
      { description: "Step simulation 10 frames", argv: ["tools", "run", "physics.simulation", "--input", "{\"steps\":10,\"deltaTime\":0.016,\"mode\":\"3d\"}"] }
    ]
  },
  {
    name: "physics.raycast-all",
    summary: "Cast a ray (3d/2d) and return ALL hits along its path, sorted by distance.",
    description: "Cast a ray (3d/2d) and return ALL hits along its path, sorted by distance. Read-only query. Use layerMask to filter.",
    group: "physics",
    inputSchema: objSchema({ originX: { type: "number" }, originY: { type: "number" }, originZ: { type: "number" }, directionX: { type: "number" }, directionY: { type: "number" }, directionZ: { type: "number" }, distance: { type: "number" }, mode: { type: "string", enum: ["3d", "2d"] }, layerMask: { type: "integer" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.raycast-all",
    docsTopic: "physics",
    equivalentUnityMcp: "physics.raycast_all",
    examples: [
      { description: "Raycast all hits forward", argv: ["tools", "run", "physics.raycast-all", "--input", "{\"originX\":0,\"originY\":0,\"originZ\":0,\"directionX\":0,\"directionY\":0,\"directionZ\":1,\"distance\":100}"] }
    ]
  },
  {
    name: "physics.linecast",
    summary: "Check if a line segment between start and end points is blocked by colliders.",
    description: "Check if a line segment between start and end points is blocked by colliders. Returns whether the line is blocked and the hit info. Read-only query. Use mode 3d/2d.",
    group: "physics",
    inputSchema: objSchema({ startX: { type: "number" }, startY: { type: "number" }, startZ: { type: "number" }, endX: { type: "number" }, endY: { type: "number" }, endZ: { type: "number" }, mode: { type: "string", enum: ["3d", "2d"] }, layerMask: { type: "integer" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.linecast",
    docsTopic: "physics",
    equivalentUnityMcp: "physics.linecast",
    examples: [
      { description: "Linecast between two points", argv: ["tools", "run", "physics.linecast", "--input", "{\"startX\":0,\"startY\":0,\"startZ\":0,\"endX\":0,\"endY\":10,\"endZ\":0}"] }
    ]
  },
  {
    name: "physics.shapecast",
    summary: "Cast a 3D sphere/box/capsule or 2D circle/box/capsule shape along a direction and return all hits.",
    description: "Cast a 3D sphere/box/capsule or 2D circle/box/capsule shape along a direction and return all hits. Read-only query. Use mode 2d for Physics2D CircleCast/BoxCast/CapsuleCast; 2D ignores originZ and directionZ.",
    group: "physics",
    inputSchema: objSchema({ mode: { type: "string", enum: ["3d", "2d"] }, shape: { type: "string", enum: ["sphere", "circle", "box", "capsule"] }, originX: { type: "number" }, originY: { type: "number" }, originZ: { type: "number" }, directionX: { type: "number" }, directionY: { type: "number" }, directionZ: { type: "number" }, distance: { type: "number" }, radius: { type: "number" }, sizeX: { type: "number" }, sizeY: { type: "number" }, angle: { type: "number" }, halfExtentX: { type: "number" }, halfExtentY: { type: "number" }, halfExtentZ: { type: "number" }, height: { type: "number" }, capsuleDirection: { type: "string", enum: ["vertical", "horizontal"] }, point1X: { type: "number" }, point1Y: { type: "number" }, point1Z: { type: "number" }, point2X: { type: "number" }, point2Y: { type: "number" }, point2Z: { type: "number" }, layerMask: { type: "integer" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.shapecast",
    docsTopic: "physics",
    equivalentUnityMcp: "physics.shapecast",
    examples: [
      { description: "Sphere cast forward", argv: ["tools", "run", "physics.shapecast", "--input", "{\"shape\":\"sphere\",\"originX\":0,\"originY\":0,\"originZ\":0,\"directionX\":0,\"directionY\":0,\"directionZ\":1,\"distance\":50,\"radius\":1}"] },
      { description: "2D circle cast to the right", argv: ["tools", "run", "physics.shapecast", "--input", "{\"mode\":\"2d\",\"shape\":\"circle\",\"originX\":0,\"originY\":0,\"directionX\":1,\"directionY\":0,\"distance\":10,\"radius\":0.5}"] }
    ]
  },
  {
    name: "physics.validate",
    summary: "Validate Rigidbody/Collider configuration on a GameObject, reporting common issues (missing Rigidbody, zero-mass, nested colliders, etc.).",
    description: "Validate Rigidbody/Collider configuration on a GameObject, reporting common issues (missing Rigidbody, zero-mass, nested colliders, etc.). Read-only. target resolves by name, hierarchy path or instance id.",
    group: "physics",
    inputSchema: objSchema({ target: { type: "string", description: "Target GameObject: name, hierarchy path or instance id." }, path: { type: "string", description: "Alias of target." } }, ["target"]),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run physics.validate",
    docsTopic: "physics",
    equivalentUnityMcp: "physics.validate",
    examples: [
      { description: "Validate a physics setup", argv: ["tools", "run", "physics.validate", "--input", "{\"target\":\"PhysicsProbe\"}"] }
    ]
  }
];
