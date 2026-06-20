/**
 * Initial registry data for the five built-in CLI extensions.
 *
 * ProBuilder has 36 fine-grained tool contributions (P7-T2a/T2b).
 * VFX Graph, Cinemachine, URP, and HDRP are registered as future extensions
 * whose tools will be implemented in P7/P8.
 */

import type { ExtensionManifest } from "./types.js";

export const PROBUILDER_MANIFEST: ExtensionManifest = {
  id: "probuilder",
  name: "ProBuilder",
  description: "3D modeling and level design tools for Unity. Provides 36 fine-grained tools for shape creation, mesh editing, vertex operations, smoothing, and boolean CSG operations through the ProBuilder package.",
  version: "1.0.0",
  unityPackages: [
    {
      packageId: "com.unity.probuilder",
      displayName: "ProBuilder"
    }
  ],
  contributions: {
    tools: [
      { name: "probuilder.ping", title: "ProBuilder Ping", description: "Check ProBuilder package availability and bridge status. Returns package info if installed.", group: "probuilder", bridgeCommand: "probuilder.ping", cliCommand: "probuilder ping" },
      { name: "probuilder.create-shape", title: "ProBuilder Create Shape", description: "Create a ProBuilder primitive shape in the scene. Supports Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism.", group: "probuilder", bridgeCommand: "probuilder.create-shape", cliCommand: "probuilder create-shape" },
      { name: "probuilder.create-poly-shape", title: "ProBuilder Create Poly Shape", description: "Create a ProBuilder mesh from a set of 2D polygon points extruded to a given height.", group: "probuilder", bridgeCommand: "probuilder.create-poly-shape", cliCommand: "probuilder create-poly-shape" },
      { name: "probuilder.extrude-faces", title: "ProBuilder Extrude Faces", description: "Extrude selected faces of a ProBuilder mesh by a given distance.", group: "probuilder", bridgeCommand: "probuilder.extrude-faces", cliCommand: "probuilder extrude-faces" },
      { name: "probuilder.delete-faces", title: "ProBuilder Delete Faces", description: "Delete selected faces from a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.delete-faces", cliCommand: "probuilder delete-faces" },
      { name: "probuilder.detach-faces", title: "ProBuilder Detach Faces", description: "Detach selected faces from a ProBuilder mesh into a new GameObject.", group: "probuilder", bridgeCommand: "probuilder.detach-faces", cliCommand: "probuilder detach-faces" },
      { name: "probuilder.merge-faces", title: "ProBuilder Merge Faces", description: "Merge selected faces of a ProBuilder mesh into a single face.", group: "probuilder", bridgeCommand: "probuilder.merge-faces", cliCommand: "probuilder merge-faces" },
      { name: "probuilder.subdivide", title: "ProBuilder Subdivide", description: "Subdivide selected faces of a ProBuilder mesh, increasing face count.", group: "probuilder", bridgeCommand: "probuilder.subdivide", cliCommand: "probuilder subdivide" },
      { name: "probuilder.flip-normals", title: "ProBuilder Flip Normals", description: "Flip the normals of selected faces on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.flip-normals", cliCommand: "probuilder flip-normals" },
      { name: "probuilder.select-faces", title: "ProBuilder Select Faces", description: "Select faces on a ProBuilder mesh using direction/grow/flood/loop/ring criteria.", group: "probuilder", bridgeCommand: "probuilder.select-faces", cliCommand: "probuilder select-faces" },
      { name: "probuilder.set-face-material", title: "ProBuilder Set Face Material", description: "Assign a material to selected faces of a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.set-face-material", cliCommand: "probuilder set-face-material" },
      { name: "probuilder.set-face-color", title: "ProBuilder Set Face Color", description: "Set the vertex color of selected faces on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.set-face-color", cliCommand: "probuilder set-face-color" },
      { name: "probuilder.set-face-uvs", title: "ProBuilder Set Face UVs", description: "Set UV parameters for selected faces on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.set-face-uvs", cliCommand: "probuilder set-face-uvs" },
      { name: "probuilder.extrude-edges", title: "ProBuilder Extrude Edges", description: "Extrude selected edges of a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.extrude-edges", cliCommand: "probuilder extrude-edges" },
      { name: "probuilder.bevel-edges", title: "ProBuilder Bevel Edges", description: "Bevel selected edges of a ProBuilder mesh by a given amount.", group: "probuilder", bridgeCommand: "probuilder.bevel-edges", cliCommand: "probuilder bevel-edges" },
      { name: "probuilder.bridge-edges", title: "ProBuilder Bridge Edges", description: "Create a bridge between two edges on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.bridge-edges", cliCommand: "probuilder bridge-edges" },
      { name: "probuilder.connect-elements", title: "ProBuilder Connect Elements", description: "Connect selected edges or faces on a ProBuilder mesh by inserting a new edge loop.", group: "probuilder", bridgeCommand: "probuilder.connect-elements", cliCommand: "probuilder connect-elements" },
      { name: "probuilder.combine-meshes", title: "ProBuilder Combine Meshes", description: "Combine multiple ProBuilder meshes into a single mesh.", group: "probuilder", bridgeCommand: "probuilder.combine-meshes", cliCommand: "probuilder combine-meshes" },
      { name: "probuilder.merge-objects", title: "ProBuilder Merge Objects", description: "Merge multiple GameObjects into a single ProBuilder object. Optionally auto-convert non-ProBuilder meshes.", group: "probuilder", bridgeCommand: "probuilder.merge-objects", cliCommand: "probuilder merge-objects" },
      { name: "probuilder.duplicate-and-flip", title: "ProBuilder Duplicate and Flip", description: "Duplicate selected faces and flip their normals.", group: "probuilder", bridgeCommand: "probuilder.duplicate-and-flip", cliCommand: "probuilder duplicate-and-flip" },
      { name: "probuilder.create-polygon", title: "ProBuilder Create Polygon", description: "Create a new polygon face from selected vertices on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.create-polygon", cliCommand: "probuilder create-polygon" },
      { name: "probuilder.merge-vertices", title: "ProBuilder Merge Vertices", description: "Merge selected vertices on a ProBuilder mesh into a single vertex.", group: "probuilder", bridgeCommand: "probuilder.merge-vertices", cliCommand: "probuilder merge-vertices" },
      { name: "probuilder.weld-vertices", title: "ProBuilder Weld Vertices", description: "Weld vertices within a radius on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.weld-vertices", cliCommand: "probuilder weld-vertices" },
      { name: "probuilder.split-vertices", title: "ProBuilder Split Vertices", description: "Split shared vertices on a ProBuilder mesh into separate vertices per face.", group: "probuilder", bridgeCommand: "probuilder.split-vertices", cliCommand: "probuilder split-vertices" },
      { name: "probuilder.move-vertices", title: "ProBuilder Move Vertices", description: "Move selected vertices on a ProBuilder mesh by an offset.", group: "probuilder", bridgeCommand: "probuilder.move-vertices", cliCommand: "probuilder move-vertices" },
      { name: "probuilder.insert-vertex", title: "ProBuilder Insert Vertex", description: "Insert a new vertex on an edge or face of a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.insert-vertex", cliCommand: "probuilder insert-vertex" },
      { name: "probuilder.append-vertices-to-edge", title: "ProBuilder Append Vertices to Edge", description: "Append new vertices along an edge of a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.append-vertices-to-edge", cliCommand: "probuilder append-vertices-to-edge" },
      { name: "probuilder.mesh-info", title: "ProBuilder Mesh Info", description: "Get detailed information about a ProBuilder mesh including vertex, face, edge counts and bounds.", group: "probuilder", bridgeCommand: "probuilder.mesh-info", cliCommand: "probuilder mesh-info" },
      { name: "probuilder.validate-mesh", title: "ProBuilder Validate Mesh", description: "Validate a ProBuilder mesh for issues like degenerate triangles and unused vertices.", group: "probuilder", bridgeCommand: "probuilder.validate-mesh", cliCommand: "probuilder validate-mesh" },
      { name: "probuilder.convert-to-probuilder", title: "ProBuilder Convert to ProBuilder", description: "Convert a regular Unity mesh to a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.convert-to-probuilder", cliCommand: "probuilder convert-to-probuilder" },
      { name: "probuilder.set-smoothing", title: "ProBuilder Set Smoothing", description: "Set the smoothing group for selected faces on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.set-smoothing", cliCommand: "probuilder set-smoothing" },
      { name: "probuilder.auto-smooth", title: "ProBuilder Auto Smooth", description: "Automatically apply smoothing to faces based on an angle threshold.", group: "probuilder", bridgeCommand: "probuilder.auto-smooth", cliCommand: "probuilder auto-smooth" },
      { name: "probuilder.center-pivot", title: "ProBuilder Center Pivot", description: "Center the pivot point of a ProBuilder mesh to its bounds center.", group: "probuilder", bridgeCommand: "probuilder.center-pivot", cliCommand: "probuilder center-pivot" },
      { name: "probuilder.freeze-transform", title: "ProBuilder Freeze Transform", description: "Bake the current transform into the ProBuilder mesh vertices, resetting the transform to identity.", group: "probuilder", bridgeCommand: "probuilder.freeze-transform", cliCommand: "probuilder freeze-transform" },
      { name: "probuilder.set-pivot", title: "ProBuilder Set Pivot", description: "Set the pivot point of a ProBuilder mesh to a specific position.", group: "probuilder", bridgeCommand: "probuilder.set-pivot", cliCommand: "probuilder set-pivot" },
      { name: "probuilder.repair-mesh", title: "ProBuilder Repair Mesh", description: "Attempt to repair a ProBuilder mesh by removing degenerate triangles and unused vertices.", group: "probuilder", bridgeCommand: "probuilder.repair-mesh", cliCommand: "probuilder repair-mesh" }
    ],
    contexts: [
      { key: "probuilder.scene", description: "ProBuilder mesh elements and operations in the current scene." }
    ],
    bridgeCommands: [
      { group: "probuilder", commands: ["ping", "create-shape", "create-poly-shape", "extrude-faces", "delete-faces", "detach-faces", "merge-faces", "subdivide", "flip-normals", "select-faces", "set-face-material", "set-face-color", "set-face-uvs", "extrude-edges", "bevel-edges", "bridge-edges", "connect-elements", "combine-meshes", "merge-objects", "duplicate-and-flip", "create-polygon", "merge-vertices", "weld-vertices", "split-vertices", "move-vertices", "insert-vertex", "append-vertices-to-edge", "mesh-info", "validate-mesh", "convert-to-probuilder", "set-smoothing", "auto-smooth", "center-pivot", "freeze-transform", "set-pivot", "repair-mesh"] }
    ],
    csharpEntries: [
      { type: "UnityCli.ProBuilder.ProBuilderUtilityTools", description: "Bridge handlers for ProBuilder ping, mesh-info, and validate-mesh." },
      { type: "UnityCli.ProBuilder.ProBuilderShapeTools", description: "Bridge handlers for ProBuilder create-shape and create-poly-shape." },
      { type: "UnityCli.ProBuilder.ProBuilderMeshEditTools", description: "Bridge handlers for ProBuilder face, edge, and mesh editing operations." },
      { type: "UnityCli.ProBuilder.ProBuilderVertexTools", description: "Bridge handlers for ProBuilder vertex operations." },
      { type: "UnityCli.ProBuilder.ProBuilderSelectionTools", description: "Bridge handlers for ProBuilder selection and material/color/UV operations." },
      { type: "UnityCli.ProBuilder.ProBuilderSmoothingTools", description: "Bridge handlers for ProBuilder smoothing, pivot, transform, and repair operations." }
    ],
    docs: [
      { topic: "probuilder", description: "ProBuilder modeling tools: shape primitives, mesh editing, vertex operations, and boolean CSG." }
    ]
  },
  tags: ["modeling", "3d", "level-design"]
};

export const VFX_GRAPH_MANIFEST: ExtensionManifest = {
  id: "vfx-graph",
  name: "VFX Graph",
  description: "Visual Effect Graph tools for creating and managing VFX assets and instances. Requires the Visual Effect Graph package.",
  version: "1.0.0",
  unityPackages: [
    {
      packageId: "com.unity.visualeffectgraph",
      displayName: "Visual Effect Graph"
    }
  ],
  contributions: {
    tools: [
      { name: "vfx.graph", title: "VFX Graph", description: "Get info on a Visual Effect graph asset, optionally create one.", group: "vfx", bridgeCommand: "vfx.graph", cliCommand: "vfx graph" },
      { name: "vfx.effect", title: "VFX Effect", description: "Manage a Visual Effect instance on a GameObject.", group: "vfx", bridgeCommand: "vfx.effect", cliCommand: "vfx effect" }
    ],
    contexts: [
      { key: "vfx.assets", description: "Visual Effect graph assets in the project." }
    ],
    bridgeCommands: [
      { group: "vfx", commands: ["graph", "effect"] }
    ],
    csharpEntries: [
      { type: "UnityCli.VfxCommands", description: "Bridge handler for VFX Graph asset and instance operations." }
    ],
    docs: [
      { topic: "vfx", description: "VFX Graph tools: graph asset management and effect instances." }
    ]
  },
  tags: ["vfx", "particles", "effects"]
};

export const CINEMACHINE_MANIFEST: ExtensionManifest = {
  id: "cinemachine",
  name: "Cinemachine",
  description: "Smart camera system for Unity. Provides camera composition, tracking, and blending through the Cinemachine package. Tools to be implemented in a future phase.",
  version: "0.1.0",
  unityPackages: [
    {
      packageId: "com.unity.cinemachine",
      displayName: "Cinemachine"
    }
  ],
  contributions: {
    tools: [],
    contexts: [
      { key: "cinemachine.cameras", description: "Cinemachine virtual cameras and composition in the current scene." }
    ],
    bridgeCommands: [],
    csharpEntries: [],
    docs: [
      { topic: "cinemachine", description: "Cinemachine smart camera system: virtual cameras, composition, and blending." }
    ]
  },
  tags: ["camera", "cinematography", "animation"]
};

export const URP_MANIFEST: ExtensionManifest = {
  id: "urp",
  name: "Universal Render Pipeline",
  description: "Universal Render Pipeline extension for Unity. Provides render pipeline asset management, shader configuration, and quality settings. Tools to be implemented in a future phase.",
  version: "0.1.0",
  unityPackages: [
    {
      packageId: "com.unity.render-pipelines.universal",
      displayName: "Universal Render Pipeline"
    }
  ],
  contributions: {
    tools: [],
    contexts: [
      { key: "urp.pipeline", description: "URP render pipeline assets, quality settings, and shader configuration." }
    ],
    bridgeCommands: [],
    csharpEntries: [],
    docs: [
      { topic: "urp", description: "Universal Render Pipeline: asset management, quality settings, and shader configuration." }
    ]
  },
  tags: ["rendering", "pipeline", "urp"]
};

export const HDRP_MANIFEST: ExtensionManifest = {
  id: "hdrp",
  name: "High Definition Render Pipeline",
  description: "High Definition Render Pipeline extension for Unity. Provides HDRP-specific lighting, volume, and rendering configuration. Tools to be implemented in a future phase.",
  version: "0.1.0",
  unityPackages: [
    {
      packageId: "com.unity.render-pipelines.high-definition",
      displayName: "High Definition Render Pipeline"
    }
  ],
  contributions: {
    tools: [],
    contexts: [
      { key: "hdrp.pipeline", description: "HDRP render pipeline assets, volume profiles, and lighting configuration." }
    ],
    bridgeCommands: [],
    csharpEntries: [],
    docs: [
      { topic: "hdrp", description: "High Definition Render Pipeline: lighting, volumes, and rendering configuration." }
    ]
  },
  tags: ["rendering", "pipeline", "hdrp"]
};

/** All built-in extension manifests. */
export const BUILTIN_EXTENSION_MANIFESTS: ExtensionManifest[] = [
  PROBUILDER_MANIFEST,
  VFX_GRAPH_MANIFEST,
  CINEMACHINE_MANIFEST,
  URP_MANIFEST,
  HDRP_MANIFEST
];
