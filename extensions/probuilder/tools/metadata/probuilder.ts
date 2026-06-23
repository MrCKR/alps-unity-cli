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
  availabilityScope: "editorSession",
  requiresPackages: [PB],
  handler: "hybrid" as const,
  docsTopic: "probuilder",
  cancellationSemantics: "Live bridge execution is cooperative and usually short; --job execution can be cancelled with job cancel --id <jobId> using immediate Unity batchmode process termination semantics."
};

export const PROBUILDER_TOOLS: ToolMetadata[] = [
  // ─── Utility ───────────────────────────────────────────
  {
    name: "probuilder.ping",
    title: "ProBuilder Ping",
    description: "Check ProBuilder package availability and bridge status. Returns package info if installed.",
    ...PB_COMMON,
    inputSchema: objSchema({}),
    outputSchema: objSchema({ installed: { type: "boolean" }, meshTypeAvailable: { type: "boolean" } }),
    readOnlyHint: true,
    destructiveHint: false,
    cliCommand: "tools run probuilder.ping",
    examples: [{ description: "Check ProBuilder availability", argv: ["tools", "run", "probuilder.ping"] }]
  },
  // ─── Shape Creation ────────────────────────────────────
  {
    name: "probuilder.create-shape",
    title: "ProBuilder Create Shape",
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
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    cliCommand: "tools run probuilder.create-shape",
    examples: [
      { description: "Create a ProBuilder cube", argv: ["tools", "run", "probuilder.create-shape", "Cube"] },
      { description: "Create a ProBuilder arch with radius", argv: ["tools", "run", "probuilder.create-shape", "Arch", "--radius", "2"] }
    ]
  },
  {
    name: "probuilder.create-poly-shape",
    title: "ProBuilder Create Poly Shape",
    description: "Create a ProBuilder mesh from a set of 2D polygon points extruded to a given height.",
    ...PB_COMMON,
    inputSchema: objSchema({
      points: { type: "array", items: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } }, description: "Polygon vertices" },
      extrudeHeight: { type: "number", description: "Extrusion height" },
      flipNormals: { type: "boolean", description: "Flip face normals" },
      name: { type: "string", description: "Name for the created GameObject" }
    }, ["points"]),
    outputSchema: objSchema({ object: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" }, faceCount: { type: "integer" }, vertexCount: { type: "integer" } } } }),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    cliCommand: "tools run probuilder.create-poly-shape"
  },
  // ─── Face Operations ──────────────────────────────────
  {
    name: "probuilder.extrude-faces",
    title: "ProBuilder Extrude Faces",
    description: "Extrude selected faces of a ProBuilder mesh by a given distance.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject name, path or instance ID" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to extrude" },
      distance: { type: "number", description: "Extrusion distance" },
      method: { type: "string", enum: ["vertex", "faceNormal"], description: "Extrusion method" }
    }, ["target"]),
    outputSchema: objSchema({ modifiedFaceCount: { type: "integer" }, newVertexCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.extrude-faces"
  },
  {
    name: "probuilder.delete-faces",
    title: "ProBuilder Delete Faces",
    description: "Delete selected faces from a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to delete" }
    }, ["target", "faceIndices"]),
    outputSchema: objSchema({ deletedCount: { type: "integer" }, remainingFaceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: true,
    cliCommand: "tools run probuilder.delete-faces"
  },
  {
    name: "probuilder.detach-faces",
    title: "ProBuilder Detach Faces",
    description: "Detach selected faces from a ProBuilder mesh into a new GameObject.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to detach" },
      deleteSourceFaces: { type: "boolean", description: "Remove detached faces from source" }
    }, ["target", "faceIndices"]),
    outputSchema: objSchema({ detachedObject: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" } } }, faceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.detach-faces"
  },
  {
    name: "probuilder.merge-faces",
    title: "ProBuilder Merge Faces",
    description: "Merge selected faces of a ProBuilder mesh into a single face.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to merge" }
    }, ["target", "faceIndices"]),
    outputSchema: objSchema({ mergedFaceIndex: { type: "integer" }, mergedFaceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.merge-faces"
  },
  {
    name: "probuilder.subdivide",
    title: "ProBuilder Subdivide",
    description: "Subdivide selected faces of a ProBuilder mesh, increasing face count.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to subdivide (optional, defaults to all)" }
    }, ["target"]),
    outputSchema: objSchema({ beforeFaceCount: { type: "integer" }, afterFaceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.subdivide"
  },
  {
    name: "probuilder.flip-normals",
    title: "ProBuilder Flip Normals",
    description: "Flip the normals of selected faces on a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to flip (optional, defaults to all)" }
    }, ["target"]),
    outputSchema: objSchema({ flippedCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.flip-normals"
  },
  {
    name: "probuilder.select-faces",
    title: "ProBuilder Select Faces",
    description: "Select faces on a ProBuilder mesh using direction/grow/flood/loop/ring criteria.",
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
    readOnlyHint: true,
    destructiveHint: false,
    cliCommand: "tools run probuilder.select-faces"
  },
  {
    name: "probuilder.set-face-material",
    title: "ProBuilder Set Face Material",
    description: "Assign a material to selected faces of a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices (optional, defaults to all)" },
      materialPath: { type: "string", description: "Asset path of the material to assign" }
    }, ["target", "materialPath"]),
    outputSchema: objSchema({ assignedFaceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.set-face-material"
  },
  {
    name: "probuilder.set-face-color",
    title: "ProBuilder Set Face Color",
    description: "Set the vertex color of selected faces on a ProBuilder mesh.",
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
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.set-face-color"
  },
  {
    name: "probuilder.set-face-uvs",
    title: "ProBuilder Set Face UVs",
    description: "Set UV parameters for selected faces on a ProBuilder mesh.",
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
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.set-face-uvs"
  },
  // ─── Edge Operations ──────────────────────────────────
  {
    name: "probuilder.extrude-edges",
    title: "ProBuilder Extrude Edges",
    description: "Extrude selected edges of a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeIndices: { type: "array", items: { type: "integer" }, description: "Edge indices to extrude" },
      edges: { type: "array", items: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } } }, description: "Edge vertex pairs" },
      distance: { type: "number", description: "Extrusion distance" },
      asGroup: { type: "boolean", description: "Extrude edges as a group" }
    }, ["target"]),
    outputSchema: objSchema({ modifiedEdgeCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.extrude-edges"
  },
  {
    name: "probuilder.bevel-edges",
    title: "ProBuilder Bevel Edges",
    description: "Bevel selected edges of a ProBuilder mesh by a given amount.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeIndices: { type: "array", items: { type: "integer" }, description: "Edge indices to bevel" },
      edges: { type: "array", items: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } } }, description: "Edge vertex pairs" },
      amount: { type: "number", description: "Bevel amount" }
    }, ["target"]),
    outputSchema: objSchema({ modifiedEdgeCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.bevel-edges"
  },
  {
    name: "probuilder.bridge-edges",
    title: "ProBuilder Bridge Edges",
    description: "Create a bridge between two edges on a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeA: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } }, description: "First edge" },
      edgeB: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } }, description: "Second edge" },
      allowNonManifold: { type: "boolean", description: "Allow non-manifold result" }
    }, ["target", "edgeA", "edgeB"]),
    outputSchema: objSchema({ newFaceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.bridge-edges"
  },
  {
    name: "probuilder.connect-elements",
    title: "ProBuilder Connect Elements",
    description: "Connect selected edges or faces on a ProBuilder mesh by inserting a new edge loop.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeIndices: { type: "array", items: { type: "integer" }, description: "Edge indices to connect" },
      edges: { type: "array", items: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } } }, description: "Edge vertex pairs" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices to connect" }
    }, ["target"]),
    outputSchema: objSchema({ connectedCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.connect-elements"
  },
  // ─── Mesh Combine / Merge ─────────────────────────────
  {
    name: "probuilder.combine-meshes",
    title: "ProBuilder Combine Meshes",
    description: "Combine multiple ProBuilder meshes into a single mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      targets: { type: "array", items: { type: "string" }, description: "Target GameObject names/paths/IDs to combine" },
      name: { type: "string", description: "Name for the combined object" }
    }, ["targets"]),
    outputSchema: objSchema({ combinedObject: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" } } }, sourceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: true,
    cliCommand: "tools run probuilder.combine-meshes"
  },
  {
    name: "probuilder.merge-objects",
    title: "ProBuilder Merge Objects",
    description: "Merge multiple GameObjects into a single ProBuilder object. Optionally auto-convert non-ProBuilder meshes.",
    ...PB_COMMON,
    inputSchema: objSchema({
      targets: { type: "array", items: { type: "string" }, description: "Target GameObject names/paths/IDs to merge" },
      name: { type: "string", description: "Name for the merged object" },
      autoConvert: { type: "boolean", description: "Auto-convert non-ProBuilder meshes to ProBuilder", default: true }
    }, ["targets"]),
    outputSchema: objSchema({ mergedObject: { type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" } } } }),
    readOnlyHint: false,
    destructiveHint: true,
    cliCommand: "tools run probuilder.merge-objects"
  },
  {
    name: "probuilder.duplicate-and-flip",
    title: "ProBuilder Duplicate and Flip",
    description: "Duplicate selected faces and flip their normals.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices (optional, defaults to all)" }
    }, ["target"]),
    outputSchema: objSchema({ duplicatedCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.duplicate-and-flip"
  },
  {
    name: "probuilder.create-polygon",
    title: "ProBuilder Create Polygon",
    description: "Create a new polygon face from selected vertices on a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices to form the polygon" },
      unordered: { type: "boolean", description: "Vertices are not in winding order; auto-sort them" }
    }, ["target", "vertexIndices"]),
    outputSchema: objSchema({ newFaceIndex: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.create-polygon"
  },
  // ─── Vertex Operations ────────────────────────────────
  {
    name: "probuilder.merge-vertices",
    title: "ProBuilder Merge Vertices",
    description: "Merge selected vertices on a ProBuilder mesh into a single vertex.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices to merge" },
      collapseToFirst: { type: "boolean", description: "Collapse to the first vertex position" }
    }, ["target", "vertexIndices"]),
    outputSchema: objSchema({ mergedVertexCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.merge-vertices"
  },
  {
    name: "probuilder.weld-vertices",
    title: "ProBuilder Weld Vertices",
    description: "Weld vertices within a radius on a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices (optional, defaults to all)" },
      radius: { type: "number", description: "Weld radius" }
    }, ["target"]),
    outputSchema: objSchema({ weldedCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.weld-vertices"
  },
  {
    name: "probuilder.split-vertices",
    title: "ProBuilder Split Vertices",
    description: "Split shared vertices on a ProBuilder mesh into separate vertices per face.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices to split" }
    }, ["target", "vertexIndices"]),
    outputSchema: objSchema({ splitCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.split-vertices"
  },
  {
    name: "probuilder.move-vertices",
    title: "ProBuilder Move Vertices",
    description: "Move selected vertices on a ProBuilder mesh by an offset.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      vertexIndices: { type: "array", items: { type: "integer" }, description: "Vertex indices to move" },
      x: { type: "number", description: "X offset" },
      y: { type: "number", description: "Y offset" },
      z: { type: "number", description: "Z offset" }
    }, ["target", "vertexIndices"]),
    outputSchema: objSchema({ movedCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.move-vertices"
  },
  {
    name: "probuilder.insert-vertex",
    title: "ProBuilder Insert Vertex",
    description: "Insert a new vertex on an edge or face of a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edge: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } }, description: "Edge to insert on" },
      faceIndex: { type: "integer", description: "Face index to insert on" },
      point: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } }, description: "Position for the new vertex" }
    }, ["target"]),
    outputSchema: objSchema({ insertedVertexIndex: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.insert-vertex"
  },
  {
    name: "probuilder.append-vertices-to-edge",
    title: "ProBuilder Append Vertices to Edge",
    description: "Append new vertices along an edge of a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      edgeIndices: { type: "array", items: { type: "integer" }, description: "Edge indices" },
      edges: { type: "array", items: { type: "object", properties: { a: { type: "integer" }, b: { type: "integer" } } }, description: "Edge vertex pairs" },
      count: { type: "integer", description: "Number of vertices to insert" }
    }, ["target"]),
    outputSchema: objSchema({ insertedCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.append-vertices-to-edge"
  },
  // ─── Info / Inspection ────────────────────────────────
  {
    name: "probuilder.mesh-info",
    title: "ProBuilder Mesh Info",
    description: "Get detailed information about a ProBuilder mesh including vertex, face, edge counts and bounds.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      include: { type: "string", enum: ["summary", "faces", "edges", "all"], description: "Level of detail to include" }
    }, ["target"]),
    outputSchema: objSchema({ vertexCount: { type: "integer" }, faceCount: { type: "integer" }, edgeCount: { type: "integer" }, bounds: { type: "object" }, materials: { type: "array" } }),
    readOnlyHint: true,
    destructiveHint: false,
    cliCommand: "tools run probuilder.mesh-info"
  },
  {
    name: "probuilder.validate-mesh",
    title: "ProBuilder Validate Mesh",
    description: "Validate a ProBuilder mesh for issues like degenerate triangles and unused vertices.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" }
    }, ["target"]),
    outputSchema: objSchema({ healthy: { type: "boolean" }, degenerateTriangles: { type: "integer" }, unusedVertices: { type: "integer" }, issues: { type: "array", items: { type: "string" } } }),
    readOnlyHint: true,
    destructiveHint: false,
    cliCommand: "tools run probuilder.validate-mesh"
  },
  // ─── Conversion ───────────────────────────────────────
  {
    name: "probuilder.convert-to-probuilder",
    title: "ProBuilder Convert to ProBuilder",
    description: "Convert a regular Unity mesh to a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject to convert" }
    }, ["target"]),
    outputSchema: objSchema({ converted: { type: "boolean" }, vertexCount: { type: "integer" }, faceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.convert-to-probuilder"
  },
  // ─── Smoothing ────────────────────────────────────────
  {
    name: "probuilder.set-smoothing",
    title: "ProBuilder Set Smoothing",
    description: "Set the smoothing group for selected faces on a ProBuilder mesh.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices" },
      smoothingGroup: { type: "integer", description: "Smoothing group ID (0 = hard edges)" }
    }, ["target", "smoothingGroup"]),
    outputSchema: objSchema({ modifiedFaceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.set-smoothing"
  },
  {
    name: "probuilder.auto-smooth",
    title: "ProBuilder Auto Smooth",
    description: "Automatically apply smoothing to faces based on an angle threshold.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      angleThreshold: { type: "number", description: "Angle threshold in degrees for smoothing" },
      faceIndices: { type: "array", items: { type: "integer" }, description: "Face indices (optional)" }
    }, ["target"]),
    outputSchema: objSchema({ smoothedFaceCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.auto-smooth"
  },
  // ─── Pivot / Transform ────────────────────────────────
  {
    name: "probuilder.center-pivot",
    title: "ProBuilder Center Pivot",
    description: "Center the pivot point of a ProBuilder mesh to its bounds center.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" }
    }, ["target"]),
    outputSchema: objSchema({ pivotOffset: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } }, newPosition: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.center-pivot"
  },
  {
    name: "probuilder.freeze-transform",
    title: "ProBuilder Freeze Transform",
    description: "Bake the current transform into the ProBuilder mesh vertices, resetting the transform to identity.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" }
    }, ["target"]),
    outputSchema: objSchema({ bakedVertexCount: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.freeze-transform"
  },
  {
    name: "probuilder.set-pivot",
    title: "ProBuilder Set Pivot",
    description: "Set the pivot point of a ProBuilder mesh to a specific position.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" },
      x: { type: "number", description: "New pivot X position" },
      y: { type: "number", description: "New pivot Y position" },
      z: { type: "number", description: "New pivot Z position" }
    }, ["target"]),
    outputSchema: objSchema({ newPivotPosition: { type: "object", properties: { x: { type: "number" }, y: { type: "number" }, z: { type: "number" } } } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.set-pivot"
  },
  // ─── Repair ───────────────────────────────────────────
  {
    name: "probuilder.repair-mesh",
    title: "ProBuilder Repair Mesh",
    description: "Attempt to repair a ProBuilder mesh by removing degenerate triangles and unused vertices.",
    ...PB_COMMON,
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject" }
    }, ["target"]),
    outputSchema: objSchema({ removedDegenerateCount: { type: "integer" }, fixedCount: { type: "integer" }, remainingIssues: { type: "integer" } }),
    readOnlyHint: false,
    destructiveHint: false,
    cliCommand: "tools run probuilder.repair-mesh"
  }
];
