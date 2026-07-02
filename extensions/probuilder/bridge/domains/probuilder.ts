import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

/**
 * ProBuilder extension bridge specs — 36 single-action tools.
 *
 * Each CLI command is routed through the project-tool.run bridge command
 * to reach the ProBuilder handlers registered via [UnityCliTool] in the
 * Unity package. The bridgeCommand field is always "project-tool.run";
 * the toolName field carries the underlying project-tool name (e.g.
 * "probuilder.ping"). The buildArgs output is wrapped into { name, input }
 * so the C# ProjectToolRun handler receives both the tool name and its
 * JSON input payload.
 */

export const probuilderBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
  // ─── Utility ───────────────────────────────────────────
  { group: "probuilder", command: "ping", bridgeCommand: "project-tool.run", toolName: "probuilder.ping", buildArgs: probuilderPingArgs, supportsJob: false },

  // ─── Shape Creation ────────────────────────────────────
  { group: "probuilder", command: "create-shape", bridgeCommand: "project-tool.run", toolName: "probuilder.create-shape", buildArgs: probuilderCreateShapeArgs, supportsJob: true },
  { group: "probuilder", command: "create-poly-shape", bridgeCommand: "project-tool.run", toolName: "probuilder.create-poly-shape", buildArgs: probuilderCreatePolyShapeArgs, supportsJob: true },

  // ─── Face Operations ──────────────────────────────────
  { group: "probuilder", command: "extrude-faces", bridgeCommand: "project-tool.run", toolName: "probuilder.extrude-faces", buildArgs: probuilderTargetWithIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "delete-faces", bridgeCommand: "project-tool.run", toolName: "probuilder.delete-faces", buildArgs: probuilderTargetWithIndicesArgs, supportsJob: true, destructiveHint: true },
  { group: "probuilder", command: "detach-faces", bridgeCommand: "project-tool.run", toolName: "probuilder.detach-faces", buildArgs: probuilderDetachFacesArgs, supportsJob: true },
  { group: "probuilder", command: "merge-faces", bridgeCommand: "project-tool.run", toolName: "probuilder.merge-faces", buildArgs: probuilderTargetWithIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "subdivide", bridgeCommand: "project-tool.run", toolName: "probuilder.subdivide", buildArgs: probuilderTargetWithOptionalIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "flip-normals", bridgeCommand: "project-tool.run", toolName: "probuilder.flip-normals", buildArgs: probuilderTargetWithOptionalIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "select-faces", bridgeCommand: "project-tool.run", toolName: "probuilder.select-faces", buildArgs: probuilderSelectFacesArgs, supportsJob: false },
  { group: "probuilder", command: "set-face-material", bridgeCommand: "project-tool.run", toolName: "probuilder.set-face-material", buildArgs: probuilderSetFaceMaterialArgs, supportsJob: true },
  { group: "probuilder", command: "set-face-color", bridgeCommand: "project-tool.run", toolName: "probuilder.set-face-color", buildArgs: probuilderSetFaceColorArgs, supportsJob: true },
  { group: "probuilder", command: "set-face-uvs", bridgeCommand: "project-tool.run", toolName: "probuilder.set-face-uvs", buildArgs: probuilderSetFaceUvsArgs, supportsJob: true },

  // ─── Edge Operations ──────────────────────────────────
  { group: "probuilder", command: "extrude-edges", bridgeCommand: "project-tool.run", toolName: "probuilder.extrude-edges", buildArgs: probuilderEdgeArgs, supportsJob: true },
  { group: "probuilder", command: "bevel-edges", bridgeCommand: "project-tool.run", toolName: "probuilder.bevel-edges", buildArgs: probuilderBevelArgs, supportsJob: true },
  { group: "probuilder", command: "bridge-edges", bridgeCommand: "project-tool.run", toolName: "probuilder.bridge-edges", buildArgs: probuilderBridgeEdgesArgs, supportsJob: true },
  { group: "probuilder", command: "connect-elements", bridgeCommand: "project-tool.run", toolName: "probuilder.connect-elements", buildArgs: probuilderConnectArgs, supportsJob: true },

  // ─── Mesh Combine / Merge ─────────────────────────────
  { group: "probuilder", command: "combine-meshes", bridgeCommand: "project-tool.run", toolName: "probuilder.combine-meshes", buildArgs: probuilderMultiTargetArgs, supportsJob: true, destructiveHint: true },
  { group: "probuilder", command: "merge-objects", bridgeCommand: "project-tool.run", toolName: "probuilder.merge-objects", buildArgs: probuilderMergeObjectsArgs, supportsJob: true, destructiveHint: true },
  { group: "probuilder", command: "duplicate-and-flip", bridgeCommand: "project-tool.run", toolName: "probuilder.duplicate-and-flip", buildArgs: probuilderTargetWithOptionalIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "create-polygon", bridgeCommand: "project-tool.run", toolName: "probuilder.create-polygon", buildArgs: probuilderCreatePolygonArgs, supportsJob: true },

  // ─── Vertex Operations ────────────────────────────────
  { group: "probuilder", command: "merge-vertices", bridgeCommand: "project-tool.run", toolName: "probuilder.merge-vertices", buildArgs: probuilderVertexIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "weld-vertices", bridgeCommand: "project-tool.run", toolName: "probuilder.weld-vertices", buildArgs: probuilderWeldArgs, supportsJob: true },
  { group: "probuilder", command: "split-vertices", bridgeCommand: "project-tool.run", toolName: "probuilder.split-vertices", buildArgs: probuilderVertexIndicesArgs, supportsJob: true },
  { group: "probuilder", command: "move-vertices", bridgeCommand: "project-tool.run", toolName: "probuilder.move-vertices", buildArgs: probuilderMoveVerticesArgs, supportsJob: true },
  { group: "probuilder", command: "insert-vertex", bridgeCommand: "project-tool.run", toolName: "probuilder.insert-vertex", buildArgs: probuilderInsertVertexArgs, supportsJob: true },
  { group: "probuilder", command: "append-vertices-to-edge", bridgeCommand: "project-tool.run", toolName: "probuilder.append-vertices-to-edge", buildArgs: probuilderAppendVerticesArgs, supportsJob: true },

  // ─── Info / Inspection ────────────────────────────────
  { group: "probuilder", command: "mesh-info", bridgeCommand: "project-tool.run", toolName: "probuilder.mesh-info", buildArgs: probuilderMeshInfoArgs, supportsJob: false },
  { group: "probuilder", command: "validate-mesh", bridgeCommand: "project-tool.run", toolName: "probuilder.validate-mesh", buildArgs: probuilderTargetOnlyArgs, supportsJob: false },

  // ─── Conversion ───────────────────────────────────────
  { group: "probuilder", command: "convert-to-probuilder", bridgeCommand: "project-tool.run", toolName: "probuilder.convert-to-probuilder", buildArgs: probuilderTargetOnlyArgs, supportsJob: true },

  // ─── Smoothing ────────────────────────────────────────
  { group: "probuilder", command: "set-smoothing", bridgeCommand: "project-tool.run", toolName: "probuilder.set-smoothing", buildArgs: probuilderSetSmoothingArgs, supportsJob: true },
  { group: "probuilder", command: "auto-smooth", bridgeCommand: "project-tool.run", toolName: "probuilder.auto-smooth", buildArgs: probuilderAutoSmoothArgs, supportsJob: true },

  // ─── Pivot / Transform ────────────────────────────────
  { group: "probuilder", command: "center-pivot", bridgeCommand: "project-tool.run", toolName: "probuilder.center-pivot", buildArgs: probuilderTargetOnlyArgs, supportsJob: true },
  { group: "probuilder", command: "freeze-transform", bridgeCommand: "project-tool.run", toolName: "probuilder.freeze-transform", buildArgs: probuilderTargetOnlyArgs, supportsJob: true },
  { group: "probuilder", command: "set-pivot", bridgeCommand: "project-tool.run", toolName: "probuilder.set-pivot", buildArgs: probuilderSetPivotArgs, supportsJob: true },

  // ─── Repair ───────────────────────────────────────────
  { group: "probuilder", command: "repair-mesh", bridgeCommand: "project-tool.run", toolName: "probuilder.repair-mesh", buildArgs: probuilderTargetOnlyArgs, supportsJob: true }
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

// ─── Arg builder functions ──────────────────────────────────

function probuilderPingArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({});
}

function probuilderCreateShapeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    shapeType: positionalTarget ?? stringFlag(flags, "shape-type") ?? stringFlag(flags, "shape") ?? stringFlag(flags, "type") ?? stringField(flags, "shapeType"),
    name: stringFlag(flags, "name") ?? stringField(flags, "name"),
    position: positionalPosition(flags),
    width: floatFlag(flags, "width") ?? numberField(flags, "width"),
    height: floatFlag(flags, "height") ?? numberField(flags, "height"),
    depth: floatFlag(flags, "depth") ?? numberField(flags, "depth"),
    radius: floatFlag(flags, "radius") ?? numberField(flags, "radius"),
    segments: numberFlag(flags, "segments") ?? numberField(flags, "segments"),
    sides: numberFlag(flags, "sides") ?? numberField(flags, "sides"),
    x: floatFlag(flags, "x") ?? numberField(flags, "x"),
    y: floatFlag(flags, "y") ?? numberField(flags, "y"),
    z: floatFlag(flags, "z") ?? numberField(flags, "z"),
    parent: stringFlag(flags, "parent") ?? stringField(flags, "parent")
  });
}

function probuilderCreatePolyShapeArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    points: jsonField(flags, "points", "points"),
    extrudeHeight: floatFlag(flags, "extrude-height") ?? floatFlag(flags, "height") ?? numberField(flags, "extrudeHeight"),
    flipNormals: boolFlag(flags, "flip-normals") ?? boolField(flags, "flipNormals"),
    name: stringFlag(flags, "name") ?? stringField(flags, "name")
  });
}

function probuilderTargetWithIndicesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices")
  });
}

function probuilderTargetWithOptionalIndicesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices")
  });
}

function probuilderDetachFacesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices"),
    deleteSourceFaces: boolFlag(flags, "delete-source-faces") ?? boolField(flags, "deleteSourceFaces")
  });
}

function probuilderSelectFacesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    direction: stringFlag(flags, "direction") ?? stringField(flags, "direction"),
    grow: numberFlag(flags, "grow") ?? numberField(flags, "grow"),
    flood: boolFlag(flags, "flood") ?? boolField(flags, "flood"),
    loop: boolFlag(flags, "loop") ?? boolField(flags, "loop"),
    ring: boolFlag(flags, "ring") ?? boolField(flags, "ring")
  });
}

function probuilderSetFaceMaterialArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices"),
    materialPath: stringFlag(flags, "material-path") ?? stringFlag(flags, "material") ?? stringField(flags, "materialPath")
  });
}

function probuilderSetFaceColorArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices"),
    r: floatFlag(flags, "r") ?? numberField(flags, "r"),
    g: floatFlag(flags, "g") ?? numberField(flags, "g"),
    b: floatFlag(flags, "b") ?? numberField(flags, "b"),
    a: floatFlag(flags, "a") ?? numberField(flags, "a")
  });
}

function probuilderSetFaceUvsArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices"),
    scale: floatFlag(flags, "scale") ?? numberField(flags, "scale"),
    offsetU: floatFlag(flags, "offset-u") ?? numberField(flags, "offsetU"),
    offsetV: floatFlag(flags, "offset-v") ?? numberField(flags, "offsetV"),
    rotation: floatFlag(flags, "rotation") ?? numberField(flags, "rotation"),
    flipU: boolFlag(flags, "flip-u") ?? boolField(flags, "flipU"),
    flipV: boolFlag(flags, "flip-v") ?? boolField(flags, "flipV")
  });
}

function probuilderEdgeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    edgeIndices: jsonField(flags, "edge-indices", "edgeIndices"),
    edges: jsonField(flags, "edges", "edges"),
    distance: floatFlag(flags, "distance") ?? numberField(flags, "distance"),
    asGroup: boolFlag(flags, "as-group") ?? boolField(flags, "asGroup")
  });
}

function probuilderBevelArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    edgeIndices: jsonField(flags, "edge-indices", "edgeIndices"),
    edges: jsonField(flags, "edges", "edges"),
    amount: floatFlag(flags, "amount") ?? numberField(flags, "amount")
  });
}

function probuilderBridgeEdgesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    edgeA: jsonField(flags, "edge-a", "edgeA"),
    edgeB: jsonField(flags, "edge-b", "edgeB"),
    allowNonManifold: boolFlag(flags, "allow-non-manifold") ?? boolField(flags, "allowNonManifold")
  });
}

function probuilderConnectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    edgeIndices: jsonField(flags, "edge-indices", "edgeIndices"),
    edges: jsonField(flags, "edges", "edges"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices")
  });
}

function probuilderMultiTargetArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    targets: jsonField(flags, "targets", "targets"),
    name: stringFlag(flags, "name") ?? stringField(flags, "name")
  });
}

function probuilderMergeObjectsArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    targets: jsonField(flags, "targets", "targets"),
    name: stringFlag(flags, "name") ?? stringField(flags, "name"),
    autoConvert: boolFlag(flags, "auto-convert") ?? boolField(flags, "autoConvert") ?? true
  });
}

function probuilderCreatePolygonArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    vertexIndices: jsonField(flags, "vertex-indices", "vertexIndices"),
    unordered: boolFlag(flags, "unordered") ?? boolField(flags, "unordered")
  });
}

function probuilderVertexIndicesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    vertexIndices: jsonField(flags, "vertex-indices", "vertexIndices")
  });
}

function probuilderWeldArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    vertexIndices: jsonField(flags, "vertex-indices", "vertexIndices"),
    radius: floatFlag(flags, "radius") ?? numberField(flags, "radius")
  });
}

function probuilderMoveVerticesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    vertexIndices: jsonField(flags, "vertex-indices", "vertexIndices"),
    x: floatFlag(flags, "x") ?? numberField(flags, "x"),
    y: floatFlag(flags, "y") ?? numberField(flags, "y"),
    z: floatFlag(flags, "z") ?? numberField(flags, "z")
  });
}

function probuilderInsertVertexArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    edge: jsonField(flags, "edge", "edge"),
    faceIndex: numberFlag(flags, "face-index") ?? numberField(flags, "faceIndex"),
    point: jsonField(flags, "point", "point"),
    x: floatFlag(flags, "x") ?? numberField(flags, "x"),
    y: floatFlag(flags, "y") ?? numberField(flags, "y"),
    z: floatFlag(flags, "z") ?? numberField(flags, "z")
  });
}

function probuilderAppendVerticesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    edgeIndices: jsonField(flags, "edge-indices", "edgeIndices"),
    edges: jsonField(flags, "edges", "edges"),
    count: numberFlag(flags, "count") ?? numberField(flags, "count")
  });
}

function probuilderMeshInfoArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    include: stringFlag(flags, "include") ?? stringField(flags, "include") ?? "summary"
  });
}

function probuilderTargetOnlyArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target")
  });
}

function probuilderSetSmoothingArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices"),
    smoothingGroup: numberFlag(flags, "smoothing-group") ?? numberField(flags, "smoothingGroup")
  });
}

function probuilderAutoSmoothArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    angleThreshold: floatFlag(flags, "angle-threshold") ?? floatFlag(flags, "angle") ?? numberField(flags, "angleThreshold"),
    faceIndices: jsonField(flags, "face-indices", "faceIndices")
  });
}

function probuilderSetPivotArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    x: floatFlag(flags, "x") ?? numberField(flags, "x"),
    y: floatFlag(flags, "y") ?? numberField(flags, "y"),
    z: floatFlag(flags, "z") ?? numberField(flags, "z")
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

// 读取 tools run --input 中无法映射为简单 flag 的数组/对象字段。
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

// 优先使用显式 CLI flag；否则保留 --input 中的结构化 JSON 值。
function jsonField(flags: BridgeFlags, flagName: string, fieldName: string): unknown {
  const raw = stringFlag(flags, flagName);
  if (raw !== undefined) return parseJsonFlag(raw);
  return structuredInput(flags)[fieldName];
}

function stringField(flags: BridgeFlags, fieldName: string): string | undefined {
  const value = structuredInput(flags)[fieldName];
  return typeof value === "string" ? value : undefined;
}

function numberField(flags: BridgeFlags, fieldName: string): number | undefined {
  const value = structuredInput(flags)[fieldName];
  return typeof value === "number" ? value : undefined;
}

function boolField(flags: BridgeFlags, fieldName: string): boolean | undefined {
  const value = structuredInput(flags)[fieldName];
  return typeof value === "boolean" ? value : undefined;
}

function positionalPosition(flags: BridgeFlags): Record<string, unknown> | undefined {
  const structuredPosition = structuredInput(flags).position;
  if (typeof structuredPosition === "object" && structuredPosition !== null && !Array.isArray(structuredPosition)) {
    return structuredPosition as Record<string, unknown>;
  }
  const x = floatFlag(flags, "x");
  const y = floatFlag(flags, "y");
  const z = floatFlag(flags, "z");
  if (x !== undefined || y !== undefined || z !== undefined) {
    return compactArgs({ x, y, z });
  }
  return undefined;
}
