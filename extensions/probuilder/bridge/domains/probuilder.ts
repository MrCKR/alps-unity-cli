import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

/**
 * ProBuilder extension bridge specs — 36 single-action tools.
 *
 * Each CLI command maps to a bridge command with explicit arg builders.
 * Replaces the previous 4 coarse-grained probuilder bridge specs.
 *
 * Source: P7-T1 alignment matrix
 */

export const probuilderBridgeSpecs: BridgeCommandSpec[] = [
  // ─── Utility ───────────────────────────────────────────
  { group: "probuilder", command: "ping", bridgeCommand: "probuilder.ping", buildArgs: probuilderPingArgs, supportsJob: false },

  // ─── Shape Creation ────────────────────────────────────
  { group: "probuilder", command: "create-shape", bridgeCommand: "probuilder.create-shape", buildArgs: probuilderCreateShapeArgs, supportsJob: true },
  { group: "probuilder", command: "create-poly-shape", bridgeCommand: "probuilder.create-poly-shape", buildArgs: probuilderCreatePolyShapeArgs, supportsJob: true },

  // ─── Face Operations ──────────────────────────────────
  { group: "probuilder", command: "extrude-faces", bridgeCommand: "probuilder.extrude-faces", buildArgs: probuilderTargetWithIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "delete-faces", bridgeCommand: "probuilder.delete-faces", buildArgs: probuilderTargetWithIndicesArgs, supportsJob: true, destructiveHint: true },
  { group: "probuilder", command: "detach-faces", bridgeCommand: "probuilder.detach-faces", buildArgs: probuilderDetachFacesArgs, supportsJob: true },
  { group: "probuilder", command: "merge-faces", bridgeCommand: "probuilder.merge-faces", buildArgs: probuilderTargetWithIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "subdivide", bridgeCommand: "probuilder.subdivide", buildArgs: probuilderTargetWithOptionalIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "flip-normals", bridgeCommand: "probuilder.flip-normals", buildArgs: probuilderTargetWithOptionalIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "select-faces", bridgeCommand: "probuilder.select-faces", buildArgs: probuilderSelectFacesArgs, supportsJob: false },
  { group: "probuilder", command: "set-face-material", bridgeCommand: "probuilder.set-face-material", buildArgs: probuilderSetFaceMaterialArgs, supportsJob: true },
  { group: "probuilder", command: "set-face-color", bridgeCommand: "probuilder.set-face-color", buildArgs: probuilderSetFaceColorArgs, supportsJob: true },
  { group: "probuilder", command: "set-face-uvs", bridgeCommand: "probuilder.set-face-uvs", buildArgs: probuilderSetFaceUvsArgs, supportsJob: true },

  // ─── Edge Operations ──────────────────────────────────
  { group: "probuilder", command: "extrude-edges", bridgeCommand: "probuilder.extrude-edges", buildArgs: probuilderEdgeArgs, supportsJob: true },
  { group: "probuilder", command: "bevel-edges", bridgeCommand: "probuilder.bevel-edges", buildArgs: probuilderBevelArgs, supportsJob: true },
  { group: "probuilder", command: "bridge-edges", bridgeCommand: "probuilder.bridge-edges", buildArgs: probuilderBridgeEdgesArgs, supportsJob: true },
  { group: "probuilder", command: "connect-elements", bridgeCommand: "probuilder.connect-elements", buildArgs: probuilderConnectArgs, supportsJob: true },

  // ─── Mesh Combine / Merge ─────────────────────────────
  { group: "probuilder", command: "combine-meshes", bridgeCommand: "probuilder.combine-meshes", buildArgs: probuilderMultiTargetArgs, supportsJob: true, destructiveHint: true },
  { group: "probuilder", command: "merge-objects", bridgeCommand: "probuilder.merge-objects", buildArgs: probuilderMergeObjectsArgs, supportsJob: true, destructiveHint: true },
  { group: "probuilder", command: "duplicate-and-flip", bridgeCommand: "probuilder.duplicate-and-flip", buildArgs: probuilderTargetWithOptionalIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "create-polygon", bridgeCommand: "probuilder.create-polygon", buildArgs: probuilderCreatePolygonArgs, supportsJob: true },

  // ─── Vertex Operations ────────────────────────────────
  { group: "probuilder", command: "merge-vertices", bridgeCommand: "probuilder.merge-vertices", buildArgs: probuilderVertexIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "weld-vertices", bridgeCommand: "probuilder.weld-vertices", buildArgs: probuilderWeldArgs, supportsJob: true },
  { group: "probuilder", command: "split-vertices", bridgeCommand: "probuilder.split-vertices", buildArgs: probuilderVertexIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "move-vertices", bridgeCommand: "probuilder.move-vertices", buildArgs: probuilderMoveVerticesArgs, supportsJob: true },
  { group: "probuilder", command: "insert-vertex", bridgeCommand: "probuilder.insert-vertex", buildArgs: probuilderInsertVertexArgs, supportsJob: true },
  { group: "probuilder", command: "append-vertices-to-edge", bridgeCommand: "probuilder.append-vertices-to-edge", buildArgs: probuilderAppendVerticesArgs, supportsJob: true },

  // ─── Info / Inspection ────────────────────────────────
  { group: "probuilder", command: "mesh-info", bridgeCommand: "probuilder.mesh-info", buildArgs: probuilderMeshInfoArgs, supportsJob: false },
  { group: "probuilder", command: "validate-mesh", bridgeCommand: "probuilder.validate-mesh", buildArgs: probuilderTargetOnlyArgs, supportsJob: false },

  // ─── Conversion ───────────────────────────────────────
  { group: "probuilder", command: "convert-to-probuilder", bridgeCommand: "probuilder.convert-to-probuilder", buildArgs: probuilderTargetOnlyArgs, supportsJob: true },

  // ─── Smoothing ────────────────────────────────────────
  { group: "probuilder", command: "set-smoothing", bridgeCommand: "probuilder.set-smoothing", buildArgs: probuilderSetSmoothingArgs, supportsJob: true },
  { group: "probuilder", command: "auto-smooth", bridgeCommand: "probuilder.auto-smooth", buildArgs: probuilderAutoSmoothArgs, supportsJob: true },

  // ─── Pivot / Transform ────────────────────────────────
  { group: "probuilder", command: "center-pivot", bridgeCommand: "probuilder.center-pivot", buildArgs: probuilderTargetOnlyArgs, supportsJob: true },
  { group: "probuilder", command: "freeze-transform", bridgeCommand: "probuilder.freeze-transform", buildArgs: probuilderTargetOnlyArgs, supportsJob: true },
  { group: "probuilder", command: "set-pivot", bridgeCommand: "probuilder.set-pivot", buildArgs: probuilderSetPivotArgs, supportsJob: true },

  // ─── Repair ───────────────────────────────────────────
  { group: "probuilder", command: "repair-mesh", bridgeCommand: "probuilder.repair-mesh", buildArgs: probuilderTargetOnlyArgs, supportsJob: true }
];

// ─── Arg builder functions ──────────────────────────────────

function probuilderPingArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({});
}

function probuilderCreateShapeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    shapeType: positionalTarget ?? stringFlag(flags, "shape-type") ?? stringFlag(flags, "shape") ?? stringFlag(flags, "type"),
    name: stringFlag(flags, "name"),
    position: positionalPosition(flags),
    width: floatFlag(flags, "width"),
    height: floatFlag(flags, "height"),
    depth: floatFlag(flags, "depth"),
    radius: floatFlag(flags, "radius"),
    segments: numberFlag(flags, "segments"),
    sides: numberFlag(flags, "sides"),
    x: floatFlag(flags, "x"),
    y: floatFlag(flags, "y"),
    z: floatFlag(flags, "z"),
    parent: stringFlag(flags, "parent")
  });
}

function probuilderCreatePolyShapeArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    points: stringFlag(flags, "points") ? parseJsonFlag(stringFlag(flags, "points")!) : undefined,
    extrudeHeight: floatFlag(flags, "extrude-height") ?? floatFlag(flags, "height"),
    flipNormals: boolFlag(flags, "flip-normals"),
    name: stringFlag(flags, "name")
  });
}

function probuilderTargetWithIndicesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined
  });
}

function probuilderTargetWithOptionalIndicesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined
  });
}

function probuilderDetachFacesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined,
    deleteSourceFaces: boolFlag(flags, "delete-source-faces")
  });
}

function probuilderSelectFacesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    direction: stringFlag(flags, "direction"),
    grow: numberFlag(flags, "grow"),
    flood: boolFlag(flags, "flood"),
    loop: boolFlag(flags, "loop"),
    ring: boolFlag(flags, "ring")
  });
}

function probuilderSetFaceMaterialArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined,
    materialPath: stringFlag(flags, "material-path") ?? stringFlag(flags, "material")
  });
}

function probuilderSetFaceColorArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined,
    r: floatFlag(flags, "r"),
    g: floatFlag(flags, "g"),
    b: floatFlag(flags, "b"),
    a: floatFlag(flags, "a")
  });
}

function probuilderSetFaceUvsArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined,
    scale: floatFlag(flags, "scale"),
    offsetU: floatFlag(flags, "offset-u"),
    offsetV: floatFlag(flags, "offset-v"),
    rotation: floatFlag(flags, "rotation"),
    flipU: boolFlag(flags, "flip-u"),
    flipV: boolFlag(flags, "flip-v")
  });
}

function probuilderEdgeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    edgeIndices: stringFlag(flags, "edge-indices") ? parseJsonFlag(stringFlag(flags, "edge-indices")!) : undefined,
    edges: stringFlag(flags, "edges") ? parseJsonFlag(stringFlag(flags, "edges")!) : undefined,
    distance: floatFlag(flags, "distance"),
    asGroup: boolFlag(flags, "as-group")
  });
}

function probuilderBevelArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    edgeIndices: stringFlag(flags, "edge-indices") ? parseJsonFlag(stringFlag(flags, "edge-indices")!) : undefined,
    edges: stringFlag(flags, "edges") ? parseJsonFlag(stringFlag(flags, "edges")!) : undefined,
    amount: floatFlag(flags, "amount")
  });
}

function probuilderBridgeEdgesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    edgeA: stringFlag(flags, "edge-a") ? parseJsonFlag(stringFlag(flags, "edge-a")!) : undefined,
    edgeB: stringFlag(flags, "edge-b") ? parseJsonFlag(stringFlag(flags, "edge-b")!) : undefined,
    allowNonManifold: boolFlag(flags, "allow-non-manifold")
  });
}

function probuilderConnectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    edgeIndices: stringFlag(flags, "edge-indices") ? parseJsonFlag(stringFlag(flags, "edge-indices")!) : undefined,
    edges: stringFlag(flags, "edges") ? parseJsonFlag(stringFlag(flags, "edges")!) : undefined,
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined
  });
}

function probuilderMultiTargetArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    targets: stringFlag(flags, "targets") ? parseJsonFlag(stringFlag(flags, "targets")!) : undefined,
    name: stringFlag(flags, "name")
  });
}

function probuilderMergeObjectsArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    targets: stringFlag(flags, "targets") ? parseJsonFlag(stringFlag(flags, "targets")!) : undefined,
    name: stringFlag(flags, "name"),
    autoConvert: boolFlag(flags, "auto-convert") ?? true
  });
}

function probuilderCreatePolygonArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    vertexIndices: stringFlag(flags, "vertex-indices") ? parseJsonFlag(stringFlag(flags, "vertex-indices")!) : undefined,
    unordered: boolFlag(flags, "unordered")
  });
}

function probuilderVertexIndicesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    vertexIndices: stringFlag(flags, "vertex-indices") ? parseJsonFlag(stringFlag(flags, "vertex-indices")!) : undefined
  });
}

function probuilderWeldArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    vertexIndices: stringFlag(flags, "vertex-indices") ? parseJsonFlag(stringFlag(flags, "vertex-indices")!) : undefined,
    radius: floatFlag(flags, "radius")
  });
}

function probuilderMoveVerticesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    vertexIndices: stringFlag(flags, "vertex-indices") ? parseJsonFlag(stringFlag(flags, "vertex-indices")!) : undefined,
    x: floatFlag(flags, "x"),
    y: floatFlag(flags, "y"),
    z: floatFlag(flags, "z")
  });
}

function probuilderInsertVertexArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    edge: stringFlag(flags, "edge") ? parseJsonFlag(stringFlag(flags, "edge")!) : undefined,
    faceIndex: numberFlag(flags, "face-index"),
    point: stringFlag(flags, "point") ? parseJsonFlag(stringFlag(flags, "point")!) : undefined,
    x: floatFlag(flags, "x"),
    y: floatFlag(flags, "y"),
    z: floatFlag(flags, "z")
  });
}

function probuilderAppendVerticesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    edgeIndices: stringFlag(flags, "edge-indices") ? parseJsonFlag(stringFlag(flags, "edge-indices")!) : undefined,
    edges: stringFlag(flags, "edges") ? parseJsonFlag(stringFlag(flags, "edges")!) : undefined,
    count: numberFlag(flags, "count")
  });
}

function probuilderMeshInfoArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    include: stringFlag(flags, "include") ?? "summary"
  });
}

function probuilderTargetOnlyArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target")
  });
}

function probuilderSetSmoothingArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined,
    smoothingGroup: numberFlag(flags, "smoothing-group")
  });
}

function probuilderAutoSmoothArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    angleThreshold: floatFlag(flags, "angle-threshold") ?? floatFlag(flags, "angle"),
    faceIndices: stringFlag(flags, "face-indices") ? parseJsonFlag(stringFlag(flags, "face-indices")!) : undefined
  });
}

function probuilderSetPivotArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    x: floatFlag(flags, "x"),
    y: floatFlag(flags, "y"),
    z: floatFlag(flags, "z")
  });
}

// ─── Helper functions ──────────────────────────────────────

function parseJsonFlag(raw: string): unknown {
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}

function positionalPosition(flags: Record<string, string | boolean>): Record<string, unknown> | undefined {
  const x = floatFlag(flags, "x");
  const y = floatFlag(flags, "y");
  const z = floatFlag(flags, "z");
  if (x !== undefined || y !== undefined || z !== undefined) {
    return compactArgs({ x, y, z });
  }
  return undefined;
}
