import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * ProBuilder extension tool descriptors — 36 single-action tools.
 *
 * Uses fine-grained single-action tools instead of coarse-grained probuilder.* entries.
 *
 * Unified constraints:
 *   group=probuilder, target=editor, availabilityScope=editorSession,
 *   requiresPackages=["com.unity.probuilder"], handler=hybrid,
 *   docsTopic=probuilder
 */

const PB = "com.unity.probuilder";
const PB_COMMON = {
  group: "probuilder",
  target: "editor",
  requiresPackages: [PB],
};

export const PROBUILDER_TOOLS: ToolMetadata[] = [
  // ─── Utility ───────────────────────────────────────────
  {
    name: "probuilder.ping",
    summary: "Check ProBuilder package availability and bridge status.",
    description: "Check ProBuilder package availability and bridge status. Returns package info if installed.",
    ...PB_COMMON,
    inputSchema: objSchema({}),
    outputSchema: objSchema({ installed: { type: "boolean" }, meshTypeAvailable: { type: "boolean" } }),
    destructiveHint: false,
    examples: [{ description: "Check ProBuilder availability", argv: ["tools", "run", "probuilder.ping"] }]
  },
  // ─── Shape Creation ────────────────────────────────────
  {
    name: "probuilder.create-shape",
    summary: "Create a ProBuilder primitive shape in the scene.",
    description: "Create a ProBuilder primitive shape in the scene. Supports Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism.",
    ...PB_COMMON,
    inputSchema: objSchema({
      shapeType: { type: "string", enum: ["Cube", "Cylinder", "Sphere", "Plane", "Cone", "Torus", "Pipe", "Arch", "Stair", "CurvedStair", "Door", "Prism"], description: "Shape type to create" },
      name: { type: "string", description: "Name for the created GameObject" },
      position: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } },
      width: { type: "number", description: "Shape width" },
      height: { type: "number", description: "Shape height" },
      depth: { type: "number", description: "Shape depth" },
      radius: { type: "number", description: "Shape radius (for Sphere, Cylinder, Cone, Torus, Pipe, Arch)" },
      segments: { type: "integer", description: "Number of segments" },
      sides: { type: "integer", description: "Number of sides" }
    }, ["shapeType"]),
    outputSchema: objSchema({ object: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" }, shape: { type: "string" }, hasProBuilderMesh: { type: "boolean" } } } }),
    destructiveHint: false,
    examples: [
      { description: "Create a ProBuilder cube", argv: ["tools", "run", "probuilder.create-shape", "Cube"] },
      { description: "Create a ProBuilder arch with radius", argv: ["tools", "run", "probuilder.create-shape", "Arch", "--radius", "2"] }
    ]
  },
  {
    name: "probuilder.create-poly-shape",
    summary: "Create a ProBuilder mesh from a set of 2D polygon points extruded to a given height.",
    description: "Create a ProBuilder mesh from supplied polygon points and extrude it to the requested height. This creates a scene object and returns counts useful for later mesh edits.",
    ...PB_COMMON,
    inputSchema: objSchema({
      points: { type: "array", items: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } }, description: "Polygon vertices" },
      extrudeHeight: { type: "number", description: "Extrusion height" },
      flipNormals: { type: "boolean", description: "Flip face normals" },
      name: { type: "string", description: "Name for the created GameObject" }
    }, ["points"]),
    outputSchema: objSchema({ object: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" }, faceCount: { type: "integer" }, vertexCount: { type: "integer" } } } }),
    destructiveHint: false,
    examples: [
      { description: "Create an extruded rectangular poly shape", argv: ["tools", "run", "probuilder.create-poly-shape", "--input", "{\"points\":[{\"x\":0,\"y\":0,\"z\":0},{\"x\":2,\"y\":0,\"z\":0},{\"x\":2,\"y\":0,\"z\":2},{\"x\":0,\"y\":0,\"z\":2}],\"extrudeHeight\":1,\"name\":\"PolyShapeProbe\"}"] }
    ],
  },
  // ─── Face Operations ──────────────────────────────────
  {
    name: "probuilder.extrude-faces",
    summary: "Extrude selected faces of a ProBuilder mesh by a given distance.",
    description: "Extrude selected faces of a ProBuilder mesh by a given distance. Use faceIndices from probuilder.mesh-info or the current selection; this modifies scene mesh geometry.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject name, path or instance ID" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to extrude" },
      distance: { type: "number", description: "Extrusion distance" },
      method: { type: "string", enum: ["vertex", "faceNormal"], description: "Extrusion method" }
    }, ["target"]),
    outputSchema: objSchema({ modifiedFaceCount: { type: "integer" }, newVertexCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.delete-faces",
    summary: "Delete selected faces from a ProBuilder mesh.",
    description: "Delete selected faces from a ProBuilder mesh. This removes geometry from the target mesh; inspect face indices first and pass only the faces intended for deletion.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to delete" }
    }, ["target", "faceIndices"]),
    outputSchema: objSchema({ deletedCount: { type: "integer" }, remainingFaceCount: { type: "integer" } }),
    destructiveHint: true,
    examples: [
      { description: "Delete selected faces after inspection", argv: ["tools", "run", "probuilder.delete-faces", "--input", "{\"target\":\"ProBuilderProbe\",\"faceIndices\":[0]}", "--yes"] }
    ],
  },
  {
    name: "probuilder.detach-faces",
    summary: "Detach selected faces from a ProBuilder mesh into a new GameObject.",
    description: "Detach selected faces from a ProBuilder mesh into a new GameObject. deleteSourceFaces controls whether the source mesh keeps or removes the detached geometry.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to detach" },
      deleteSourceFaces: { type: "boolean", description: "Remove detached faces from source" }
    }, ["target", "faceIndices"]),
    outputSchema: objSchema({ detachedObject: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" } } }, faceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.merge-faces",
    summary: "Merge selected faces of a ProBuilder mesh into a single face.",
    description: "Merge selected faces of a ProBuilder mesh into a single face. Use adjacent face indices from mesh inspection to avoid invalid topology results.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to merge" }
    }, ["target", "faceIndices"]),
    outputSchema: objSchema({ mergedFaceIndex: { type: "integer" }, mergedFaceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.subdivide",
    summary: "Subdivide selected faces of a ProBuilder mesh, increasing face count.",
    description: "Subdivide selected faces of a ProBuilder mesh, increasing face count. Omit faceIndices to subdivide all faces on the target mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to subdivide (optional, defaults to all)" }
    }, ["target"]),
    outputSchema: objSchema({ beforeFaceCount: { type: "integer" }, afterFaceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.flip-normals",
    summary: "Flip the normals of selected faces on a ProBuilder mesh.",
    description: "Flip normals on selected faces of a ProBuilder mesh. Omit faceIndices to flip all faces; use this to correct inward-facing geometry.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to flip (optional, defaults to all)" }
    }, ["target"]),
    outputSchema: objSchema({ flippedCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.select-faces",
    summary: "Select faces on a ProBuilder mesh using direction/grow/flood/loop/ring criteria.",
    description: "Select faces on a ProBuilder mesh using direction, grow, flood, loop or ring criteria. This changes editor selection state and returns selected face indices for later operations.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      direction: { type: "string", enum: ["north", "south", "east", "west", "up", "down"], description: "Directional selection" },
      grow: { type: "integer", description: "Grow selection by N iterations" },
      flood: { type: "boolean", description: "Select all faces" },
      loop: { type: "boolean", description: "Select face loop" },
      ring: { type: "boolean", description: "Select face ring" }
    }, ["target"]),
    outputSchema: objSchema({ faceIndices: { type: "array", items: { type: "integer" } } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.set-face-material",
    summary: "Assign a material to selected faces of a ProBuilder mesh.",
    description: "Assign a material asset to selected faces of a ProBuilder mesh. Omit faceIndices to apply to all faces; materialPath must point to an existing material asset.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices (optional, defaults to all)" },
      materialPath: { type: "string", description: "Asset path of the material to assign" }
    }, ["target", "materialPath"]),
    outputSchema: objSchema({ assignedFaceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.set-face-color",
    summary: "Set the vertex color of selected faces on a ProBuilder mesh.",
    description: "Set vertex color on selected faces of a ProBuilder mesh. Omit faceIndices to color all faces; r/g/b/a values are normalized 0..1 channels.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices (optional, defaults to all)" },
      r: { type: "number", description: "Red (0-1)" },
      g: { type: "number", description: "Green (0-1)" },
      b: { type: "number", description: "Blue (0-1)" },
      a: { type: "number", description: "Alpha (0-1)" }
    }, ["target"]),
    outputSchema: objSchema({ coloredFaceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.set-face-uvs",
    summary: "Set UV parameters for selected faces on a ProBuilder mesh.",
    description: "Set UV parameters for selected faces on a ProBuilder mesh. Use scale, offset, rotation and flip flags to adjust selected face UVs without changing mesh topology.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices (optional, defaults to all)" },
      scale: { type: "number", description: "UV scale" },
      offset: { type: "object", properties: { u: { type: "number" }, v: { type: "number" } } },
      rotation: { type: "number", description: "UV rotation in degrees" },
      flipU: { type: "boolean", description: "Flip U coordinate" },
      flipV: { type: "boolean", description: "Flip V coordinate" }
    }, ["target"]),
    outputSchema: objSchema({ updatedFaceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  // ─── Edge Operations ──────────────────────────────────
  {
    name: "probuilder.extrude-edges",
    summary: "Extrude selected edges of a ProBuilder mesh.",
    description: "Extrude selected edges of a ProBuilder mesh. Pass edgeIndices or explicit edge vertex pairs; this modifies scene mesh topology.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeIndices: { type: "array", items: { type: "integer" }, description: "Edge indices to extrude" },
      edges: { type: "array", items: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } } }, description: "Edge vertex pairs" },
      distance: { type: "number", description: "Extrusion distance" },
      asGroup: { type: "boolean", description: "Extrude edges as a group" }
    }, ["target"]),
    outputSchema: objSchema({ modifiedEdgeCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.bevel-edges",
    summary: "Bevel selected edges of a ProBuilder mesh by a given amount.",
    description: "Bevel selected edges of a ProBuilder mesh by a given amount. Pass edgeIndices or explicit edge pairs from mesh inspection to control the edited edges.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeIndices: { type: "array", items: { type: "integer" }, description: "Edge indices to bevel" },
      edges: { type: "array", items: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } } }, description: "Edge vertex pairs" },
      amount: { type: "number", description: "Bevel amount" }
    }, ["target"]),
    outputSchema: objSchema({ modifiedEdgeCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.bridge-edges",
    summary: "Create a bridge between two edges on a ProBuilder mesh.",
    description: "Create a bridge face between two edges on a ProBuilder mesh. Pass edgeA and edgeB explicitly to avoid ambiguous topology edits.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeA: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } }, description: "First edge" },
      edgeB: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } }, description: "Second edge" },
      allowNonManifold: { type: "boolean", description: "Allow non-manifold result" }
    }, ["target", "edgeA", "edgeB"]),
    outputSchema: objSchema({ newFaceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.connect-elements",
    summary: "Connect selected edges or faces on a ProBuilder mesh by inserting a new edge loop.",
    description: "Connect selected edges or faces on a ProBuilder mesh by inserting a new edge loop. Use either edge or face selections depending on the mesh editing workflow.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeIndices: { type: "array", items: { type: "integer" }, description: "Edge indices to connect" },
      edges: { type: "array", items: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } } }, description: "Edge vertex pairs" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to connect" }
    }, ["target"]),
    outputSchema: objSchema({ connectedCount: { type: "integer" } }),
    destructiveHint: false,
  },
  // ─── Mesh Combine / Merge ─────────────────────────────
  {
    name: "probuilder.combine-meshes",
    summary: "Combine multiple ProBuilder meshes into a single mesh.",
    description: "Combine multiple ProBuilder meshes into a single scene mesh. This consumes multiple source targets into one combined object; inspect target names/paths before running.",
    ...PB_COMMON,
    inputSchema: objSchema({
      targets: { type: "array", items: { type: "string" }, description: "Target GameObject names/paths/IDs to combine" },
      name: { type: "string", description: "Name for the combined object" }
    }, ["targets"]),
    outputSchema: objSchema({ combinedObject: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" } } }, sourceCount: { type: "integer" } }),
    destructiveHint: true,
    examples: [
      { description: "Combine two ProBuilder objects", argv: ["tools", "run", "probuilder.combine-meshes", "--input", "{\"targets\":[\"ProBuilderA\",\"ProBuilderB\"],\"name\":\"CombinedProBuilder\"}", "--yes"] }
    ],
  },
  {
    name: "probuilder.merge-objects",
    summary: "Merge multiple GameObjects into a single ProBuilder object.",
    description: "Merge multiple GameObjects into a single ProBuilder object. Optionally auto-convert non-ProBuilder meshes.",
    ...PB_COMMON,
    inputSchema: objSchema({
      targets: { type: "array", items: { type: "string" }, description: "Target GameObject names/paths/IDs to merge" },
      name: { type: "string", description: "Name for the merged object" },
      autoConvert: { type: "boolean", description: "Auto-convert non-ProBuilder meshes to ProBuilder", default: true }
    }, ["targets"]),
    outputSchema: objSchema({ mergedObject: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" } } } }),
    destructiveHint: true,
    examples: [
      { description: "Merge scene meshes into one ProBuilder object", argv: ["tools", "run", "probuilder.merge-objects", "--input", "{\"targets\":[\"MeshA\",\"MeshB\"],\"name\":\"MergedProBuilder\",\"autoConvert\":true}", "--yes"] }
    ],
  },
  {
    name: "probuilder.duplicate-and-flip",
    summary: "Duplicate selected faces and flip their normals.",
    description: "Duplicate selected faces and flip their normals on a ProBuilder mesh. Omit faceIndices to duplicate all faces on the target mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices (optional, defaults to all)" }
    }, ["target"]),
    outputSchema: objSchema({ duplicatedCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.create-polygon",
    summary: "Create a new polygon face from selected vertices on a ProBuilder mesh.",
    description: "Create a new polygon face from selected vertices on a ProBuilder mesh. Provide vertex indices in winding order unless unordered=true is intended.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices to form the polygon" },
      unordered: { type: "boolean", description: "Vertices are not in winding order; auto-sort them" }
    }, ["target", "vertexIndices"]),
    outputSchema: objSchema({ newFaceIndex: { type: "integer" } }),
    destructiveHint: false,
  },
  // ─── Vertex Operations ────────────────────────────────
  {
    name: "probuilder.merge-vertices",
    summary: "Merge selected vertices on a ProBuilder mesh into a single vertex.",
    description: "Merge selected vertices on a ProBuilder mesh into a single vertex. collapseToFirst controls whether the merged position follows the first vertex.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices to merge" },
      collapseToFirst: { type: "boolean", description: "Collapse to the first vertex position" }
    }, ["target", "vertexIndices"]),
    outputSchema: objSchema({ mergedVertexCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.weld-vertices",
    summary: "Weld vertices within a radius on a ProBuilder mesh.",
    description: "Weld vertices within a radius on a ProBuilder mesh. Omit vertexIndices to consider all vertices; choose radius carefully to avoid collapsing nearby detail.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices (optional, defaults to all)" },
      radius: { type: "number", description: "Weld radius" }
    }, ["target"]),
    outputSchema: objSchema({ weldedCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.split-vertices",
    summary: "Split shared vertices on a ProBuilder mesh into separate vertices per face.",
    description: "Split shared vertices on a ProBuilder mesh into separate vertices per face. Use this before face-specific normals, colors or UV edits that must not share vertices.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices to split" }
    }, ["target", "vertexIndices"]),
    outputSchema: objSchema({ splitCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.move-vertices",
    summary: "Move selected vertices on a ProBuilder mesh by an offset.",
    description: "Move selected vertices on a ProBuilder mesh by an offset. Provide x/y/z deltas and vertex indices from mesh inspection.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices to move" },
      x: { type: "number", description: "X offset" },
      y: { type: "number", description: "Y offset" },
      z: { type: "number", description: "Z offset" }
    }, ["target", "vertexIndices"]),
    outputSchema: objSchema({ movedCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.insert-vertex",
    summary: "Insert a new vertex on an edge or face of a ProBuilder mesh.",
    description: "Insert a new vertex on an edge or face of a ProBuilder mesh. Provide edge or faceIndex plus point to control the insertion location.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edge: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } }, description: "Edge to insert on" },
      faceIndex: { type: "integer", description: "Face index to insert on" },
      point: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } }, description: "Position for the new vertex" }
    }, ["target"]),
    outputSchema: objSchema({ insertedVertexIndex: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.append-vertices-to-edge",
    summary: "Append new vertices along an edge of a ProBuilder mesh.",
    description: "Append new vertices along an edge of a ProBuilder mesh. Use edgeIndices or explicit edge pairs and count to control how many vertices are inserted.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeIndices: { type: "array", items: { type: "integer" }, description: "Edge indices" },
      edges: { type: "array", items: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } } }, description: "Edge vertex pairs" },
      count: { type: "integer", description: "Number of vertices to insert" }
    }, ["target"]),
    outputSchema: objSchema({ insertedCount: { type: "integer" } }),
    destructiveHint: false,
  },
  // ─── Info / Inspection ────────────────────────────────
  {
    name: "probuilder.mesh-info",
    summary: "Get detailed information about a ProBuilder mesh including vertex, face, edge counts and bounds.",
    description: "Read detailed ProBuilder mesh information including vertex, face, edge counts and bounds. Use include to choose summary, faces, edges or all before editing geometry.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      include: { type: "string", enum: ["summary", "faces", "edges", "all"], description: "Level of detail to include" }
    }, ["target"]),
    outputSchema: objSchema({ vertexCount: { type: "integer" }, faceCount: { type: "integer" }, edgeCount: { type: "integer" }, bounds: { type: "object" }, materials: { type: "array" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.validate-mesh",
    summary: "Validate a ProBuilder mesh for issues like degenerate triangles and unused vertices.",
    description: "Validate a ProBuilder mesh for issues like degenerate triangles and unused vertices. This is read-only and should be run before repair-mesh on suspect geometry.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" }
    }, ["target"]),
    outputSchema: objSchema({ healthy: { type: "boolean" }, degenerateTriangles: { type: "integer" }, unusedVertices: { type: "integer" }, issues: { type: "array", items: { type: "string" } } }),
    destructiveHint: false,
  },
  // ─── Conversion ───────────────────────────────────────
  {
    name: "probuilder.convert-to-probuilder",
    summary: "Convert a regular Unity mesh to a ProBuilder mesh.",
    description: "Convert a regular Unity mesh to a ProBuilder mesh so later ProBuilder edit tools can operate on it. This modifies the scene object but does not create a separate asset by itself.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject to convert" }
    }, ["target"]),
    outputSchema: objSchema({ converted: { type: "boolean" }, vertexCount: { type: "integer" }, faceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  // ─── Smoothing ────────────────────────────────────────
  {
    name: "probuilder.set-smoothing",
    summary: "Set the smoothing group for selected faces on a ProBuilder mesh.",
    description: "Set the smoothing group for selected faces on a ProBuilder mesh. smoothingGroup=0 produces hard edges; nonzero values group faces for smoothing.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices" },
      smoothingGroup: { type: "integer", description: "Smoothing group ID (0 = hard edges)" }
    }, ["target", "smoothingGroup"]),
    outputSchema: objSchema({ modifiedFaceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.auto-smooth",
    summary: "Automatically apply smoothing to faces based on an angle threshold.",
    description: "Automatically apply smoothing to faces based on an angle threshold. Omit faceIndices to process all faces on the target mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      angleThreshold: { type: "number", description: "Angle threshold in degrees for smoothing" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices (optional)" }
    }, ["target"]),
    outputSchema: objSchema({ smoothedFaceCount: { type: "integer" } }),
    destructiveHint: false,
  },
  // ─── Pivot / Transform ────────────────────────────────
  {
    name: "probuilder.center-pivot",
    summary: "Center the pivot point of a ProBuilder mesh to its bounds center.",
    description: "Center the pivot point of a ProBuilder mesh to its bounds center. This updates scene transform/mesh data and returns the pivot offset and new position.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" }
    }, ["target"]),
    outputSchema: objSchema({ pivotOffset: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } }, newPosition: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.freeze-transform",
    summary: "Bake the current transform into the ProBuilder mesh vertices, resetting the transform to identity.",
    description: "Bake the current transform into the ProBuilder mesh vertices, resetting the transform to identity. Use this when mesh geometry should keep its world shape after clearing transform values.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" }
    }, ["target"]),
    outputSchema: objSchema({ bakedVertexCount: { type: "integer" } }),
    destructiveHint: false,
  },
  {
    name: "probuilder.set-pivot",
    summary: "Set the pivot point of a ProBuilder mesh to a specific position.",
    description: "Set the pivot point of a ProBuilder mesh to a specific position. Provide x/y/z in scene coordinates and inspect the returned pivot position.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      x: { type: "number", description: "New pivot X position" },
      y: { type: "number", description: "New pivot Y position" },
      z: { type: "number", description: "New pivot Z position" }
    }, ["target"]),
    outputSchema: objSchema({ newPivotPosition: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } } }),
    destructiveHint: false,
  },
  // ─── Repair ───────────────────────────────────────────
  {
    name: "probuilder.repair-mesh",
    summary: "Attempt to repair a ProBuilder mesh by removing degenerate triangles and unused vertices.",
    description: "Attempt to repair a ProBuilder mesh by removing degenerate triangles and unused vertices. Run validate-mesh first to understand issues and inspect remainingIssues afterwards.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" }
    }, ["target"]),
    outputSchema: objSchema({ removedDegenerateCount: { type: "integer" }, fixedCount: { type: "integer" }, remainingIssues: { type: "integer" } }),
    destructiveHint: false,
  }
];
