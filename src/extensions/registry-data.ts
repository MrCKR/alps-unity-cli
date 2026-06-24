/**
 * Initial registry data for the five built-in CLI extensions.
 *
 * ProBuilder has 36 fine-grained tool contributions.
 * Cinemachine has 10 camera tools.
 * VFX Graph has 16 tools (graph, effect, parameter, control, event, template,
 *   particle system, line renderer, trail renderer).
 * URP has 3 tools (feature, volume, settings).
 * HDRP has 2 tools (volume, settings).
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
  unityInstall: {
    layout: "directory",
    include: ["extension.json", "unitypkg/", "docs/"]
  },
  contributions: {
    tools: [
      { name: "probuilder.ping", title: "ProBuilder Ping", description: "Check ProBuilder package availability and bridge status. Returns package info if installed.", group: "probuilder", bridgeCommand: "probuilder.ping", cliCommand: "tools run probuilder.ping" },
      { name: "probuilder.create-shape", title: "ProBuilder Create Shape", description: "Create a ProBuilder primitive shape in the scene. Supports Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism.", group: "probuilder", bridgeCommand: "probuilder.create-shape", cliCommand: "tools run probuilder.create-shape" },
      { name: "probuilder.create-poly-shape", title: "ProBuilder Create Poly Shape", description: "Create a ProBuilder mesh from a set of 2D polygon points extruded to a given height.", group: "probuilder", bridgeCommand: "probuilder.create-poly-shape", cliCommand: "tools run probuilder.create-poly-shape" },
      { name: "probuilder.extrude-faces", title: "ProBuilder Extrude Faces", description: "Extrude selected faces of a ProBuilder mesh by a given distance.", group: "probuilder", bridgeCommand: "probuilder.extrude-faces", cliCommand: "tools run probuilder.extrude-faces" },
      { name: "probuilder.delete-faces", title: "ProBuilder Delete Faces", description: "Delete selected faces from a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.delete-faces", cliCommand: "tools run probuilder.delete-faces" },
      { name: "probuilder.detach-faces", title: "ProBuilder Detach Faces", description: "Detach selected faces from a ProBuilder mesh into a new GameObject.", group: "probuilder", bridgeCommand: "probuilder.detach-faces", cliCommand: "tools run probuilder.detach-faces" },
      { name: "probuilder.merge-faces", title: "ProBuilder Merge Faces", description: "Merge selected faces of a ProBuilder mesh into a single face.", group: "probuilder", bridgeCommand: "probuilder.merge-faces", cliCommand: "tools run probuilder.merge-faces" },
      { name: "probuilder.subdivide", title: "ProBuilder Subdivide", description: "Subdivide selected faces of a ProBuilder mesh, increasing face count.", group: "probuilder", bridgeCommand: "probuilder.subdivide", cliCommand: "tools run probuilder.subdivide" },
      { name: "probuilder.flip-normals", title: "ProBuilder Flip Normals", description: "Flip the normals of selected faces on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.flip-normals", cliCommand: "tools run probuilder.flip-normals" },
      { name: "probuilder.select-faces", title: "ProBuilder Select Faces", description: "Select faces on a ProBuilder mesh using direction/grow/flood/loop/ring criteria.", group: "probuilder", bridgeCommand: "probuilder.select-faces", cliCommand: "tools run probuilder.select-faces" },
      { name: "probuilder.set-face-material", title: "ProBuilder Set Face Material", description: "Assign a material to selected faces of a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.set-face-material", cliCommand: "tools run probuilder.set-face-material" },
      { name: "probuilder.set-face-color", title: "ProBuilder Set Face Color", description: "Set the vertex color of selected faces on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.set-face-color", cliCommand: "tools run probuilder.set-face-color" },
      { name: "probuilder.set-face-uvs", title: "ProBuilder Set Face UVs", description: "Set UV parameters for selected faces on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.set-face-uvs", cliCommand: "tools run probuilder.set-face-uvs" },
      { name: "probuilder.extrude-edges", title: "ProBuilder Extrude Edges", description: "Extrude selected edges of a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.extrude-edges", cliCommand: "tools run probuilder.extrude-edges" },
      { name: "probuilder.bevel-edges", title: "ProBuilder Bevel Edges", description: "Bevel selected edges of a ProBuilder mesh by a given amount.", group: "probuilder", bridgeCommand: "probuilder.bevel-edges", cliCommand: "tools run probuilder.bevel-edges" },
      { name: "probuilder.bridge-edges", title: "ProBuilder Bridge Edges", description: "Create a bridge between two edges on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.bridge-edges", cliCommand: "tools run probuilder.bridge-edges" },
      { name: "probuilder.connect-elements", title: "ProBuilder Connect Elements", description: "Connect selected edges or faces on a ProBuilder mesh by inserting a new edge loop.", group: "probuilder", bridgeCommand: "probuilder.connect-elements", cliCommand: "tools run probuilder.connect-elements" },
      { name: "probuilder.combine-meshes", title: "ProBuilder Combine Meshes", description: "Combine multiple ProBuilder meshes into a single mesh.", group: "probuilder", bridgeCommand: "probuilder.combine-meshes", cliCommand: "tools run probuilder.combine-meshes" },
      { name: "probuilder.merge-objects", title: "ProBuilder Merge Objects", description: "Merge multiple GameObjects into a single ProBuilder object. Optionally auto-convert non-ProBuilder meshes.", group: "probuilder", bridgeCommand: "probuilder.merge-objects", cliCommand: "tools run probuilder.merge-objects" },
      { name: "probuilder.duplicate-and-flip", title: "ProBuilder Duplicate and Flip", description: "Duplicate selected faces and flip their normals.", group: "probuilder", bridgeCommand: "probuilder.duplicate-and-flip", cliCommand: "tools run probuilder.duplicate-and-flip" },
      { name: "probuilder.create-polygon", title: "ProBuilder Create Polygon", description: "Create a new polygon face from selected vertices on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.create-polygon", cliCommand: "tools run probuilder.create-polygon" },
      { name: "probuilder.merge-vertices", title: "ProBuilder Merge Vertices", description: "Merge selected vertices on a ProBuilder mesh into a single vertex.", group: "probuilder", bridgeCommand: "probuilder.merge-vertices", cliCommand: "tools run probuilder.merge-vertices" },
      { name: "probuilder.weld-vertices", title: "ProBuilder Weld Vertices", description: "Weld vertices within a radius on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.weld-vertices", cliCommand: "tools run probuilder.weld-vertices" },
      { name: "probuilder.split-vertices", title: "ProBuilder Split Vertices", description: "Split shared vertices on a ProBuilder mesh into separate vertices per face.", group: "probuilder", bridgeCommand: "probuilder.split-vertices", cliCommand: "tools run probuilder.split-vertices" },
      { name: "probuilder.move-vertices", title: "ProBuilder Move Vertices", description: "Move selected vertices on a ProBuilder mesh by an offset.", group: "probuilder", bridgeCommand: "probuilder.move-vertices", cliCommand: "tools run probuilder.move-vertices" },
      { name: "probuilder.insert-vertex", title: "ProBuilder Insert Vertex", description: "Insert a new vertex on an edge or face of a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.insert-vertex", cliCommand: "tools run probuilder.insert-vertex" },
      { name: "probuilder.append-vertices-to-edge", title: "ProBuilder Append Vertices to Edge", description: "Append new vertices along an edge of a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.append-vertices-to-edge", cliCommand: "tools run probuilder.append-vertices-to-edge" },
      { name: "probuilder.mesh-info", title: "ProBuilder Mesh Info", description: "Get detailed information about a ProBuilder mesh including vertex, face, edge counts and bounds.", group: "probuilder", bridgeCommand: "probuilder.mesh-info", cliCommand: "tools run probuilder.mesh-info" },
      { name: "probuilder.validate-mesh", title: "ProBuilder Validate Mesh", description: "Validate a ProBuilder mesh for issues like degenerate triangles and unused vertices.", group: "probuilder", bridgeCommand: "probuilder.validate-mesh", cliCommand: "tools run probuilder.validate-mesh" },
      { name: "probuilder.convert-to-probuilder", title: "ProBuilder Convert to ProBuilder", description: "Convert a regular Unity mesh to a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.convert-to-probuilder", cliCommand: "tools run probuilder.convert-to-probuilder" },
      { name: "probuilder.set-smoothing", title: "ProBuilder Set Smoothing", description: "Set the smoothing group for selected faces on a ProBuilder mesh.", group: "probuilder", bridgeCommand: "probuilder.set-smoothing", cliCommand: "tools run probuilder.set-smoothing" },
      { name: "probuilder.auto-smooth", title: "ProBuilder Auto Smooth", description: "Automatically apply smoothing to faces based on an angle threshold.", group: "probuilder", bridgeCommand: "probuilder.auto-smooth", cliCommand: "tools run probuilder.auto-smooth" },
      { name: "probuilder.center-pivot", title: "ProBuilder Center Pivot", description: "Center the pivot point of a ProBuilder mesh to its bounds center.", group: "probuilder", bridgeCommand: "probuilder.center-pivot", cliCommand: "tools run probuilder.center-pivot" },
      { name: "probuilder.freeze-transform", title: "ProBuilder Freeze Transform", description: "Bake the current transform into the ProBuilder mesh vertices, resetting the transform to identity.", group: "probuilder", bridgeCommand: "probuilder.freeze-transform", cliCommand: "tools run probuilder.freeze-transform" },
      { name: "probuilder.set-pivot", title: "ProBuilder Set Pivot", description: "Set the pivot point of a ProBuilder mesh to a specific position.", group: "probuilder", bridgeCommand: "probuilder.set-pivot", cliCommand: "tools run probuilder.set-pivot" },
      { name: "probuilder.repair-mesh", title: "ProBuilder Repair Mesh", description: "Attempt to repair a ProBuilder mesh by removing degenerate triangles and unused vertices.", group: "probuilder", bridgeCommand: "probuilder.repair-mesh", cliCommand: "tools run probuilder.repair-mesh" }
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
  unityInstall: {
    layout: "directory",
    include: ["extension.json", "unitypkg/", "docs/"]
  },
  contributions: {
    tools: [
      { name: "vfx.graph", title: "VFX Graph", description: "Get info on a Visual Effect graph asset, optionally create one.", group: "vfx", bridgeCommand: "vfx.graph", cliCommand: "tools run vfx.graph" },
      { name: "vfx.effect", title: "VFX Effect", description: "Manage a Visual Effect instance on a GameObject.", group: "vfx", bridgeCommand: "vfx.effect", cliCommand: "tools run vfx.effect" }
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
  description: "Smart camera system for Unity. Provides camera composition, tracking, blending, noise, and extension management through the Cinemachine package.",
  version: "1.0.0",
  unityPackages: [
    {
      packageId: "com.unity.cinemachine",
      displayName: "Cinemachine"
    }
  ],
  unityInstall: {
    layout: "directory",
    include: ["extension.json", "unitypkg/", "docs/"]
  },
  contributions: {
    tools: [
      { name: "cinemachine.create", title: "Cinemachine Create", description: "Create a Cinemachine virtual camera with an optional preset (follow, third_person, freelook, dolly, static, top_down, side_scroller).", group: "cinemachine", bridgeCommand: "cinemachine.create", cliCommand: "tools run cinemachine.create" },
      { name: "cinemachine.target", title: "Cinemachine Target", description: "Set the Follow and/or LookAt targets on a Cinemachine virtual camera.", group: "cinemachine", bridgeCommand: "cinemachine.target", cliCommand: "tools run cinemachine.target" },
      { name: "cinemachine.lens", title: "Cinemachine Lens", description: "Get or set lens properties (fieldOfView, nearClipPlane, farClipPlane, orthographicSize, dutch) on a Cinemachine virtual camera.", group: "cinemachine", bridgeCommand: "cinemachine.lens", cliCommand: "tools run cinemachine.lens" },
      { name: "cinemachine.priority", title: "Cinemachine Priority", description: "Get or set the priority (and enabled flag) on a Cinemachine virtual camera.", group: "cinemachine", bridgeCommand: "cinemachine.priority", cliCommand: "tools run cinemachine.priority" },
      { name: "cinemachine.blend", title: "Cinemachine Blend", description: "Get or set the DefaultBlend (style + duration) on the CinemachineBrain of a target camera owner.", group: "cinemachine", bridgeCommand: "cinemachine.blend", cliCommand: "tools run cinemachine.blend" },
      { name: "cinemachine.body", title: "Cinemachine Body", description: "Swap or configure the Cinemachine Body pipeline component on a virtual camera.", group: "cinemachine", bridgeCommand: "cinemachine.body", cliCommand: "tools run cinemachine.body" },
      { name: "cinemachine.aim", title: "Cinemachine Aim", description: "Swap or configure the Cinemachine Aim pipeline component on a virtual camera.", group: "cinemachine", bridgeCommand: "cinemachine.aim", cliCommand: "tools run cinemachine.aim" },
      { name: "cinemachine.noise", title: "Cinemachine Noise", description: "Add, configure or remove a CinemachineBasicMultiChannelPerlin noise component on a virtual camera.", group: "cinemachine", bridgeCommand: "cinemachine.noise", cliCommand: "tools run cinemachine.noise" },
      { name: "cinemachine.brain", title: "Cinemachine Brain", description: "Ensure a CinemachineBrain is attached to the target camera owner, or report the brain's active virtual camera and blending state.", group: "cinemachine", bridgeCommand: "cinemachine.brain", cliCommand: "tools run cinemachine.brain" },
      { name: "cinemachine.extension", title: "Cinemachine Extension", description: "List, add or remove CinemachineExtension-derived components on a virtual camera.", group: "cinemachine", bridgeCommand: "cinemachine.extension", cliCommand: "tools run cinemachine.extension" }
    ],
    contexts: [
      { key: "cinemachine.cameras", description: "Cinemachine virtual cameras and composition in the current scene." }
    ],
    bridgeCommands: [
      { group: "cinemachine", commands: ["create", "target", "lens", "priority", "blend", "body", "aim", "noise", "brain", "extension"] }
    ],
    csharpEntries: [
      { type: "UnityCli.Cinemachine.CinemachineCameraTools", description: "Bridge handlers for Cinemachine create, target, lens, priority, blend, body, aim, noise, brain, and extension operations." }
    ],
    docs: [
      { topic: "cinemachine", description: "Cinemachine smart camera system: virtual cameras, composition, blending, noise, and extensions." }
    ]
  },
  tags: ["camera", "cinematography", "animation"]
};

export const URP_MANIFEST: ExtensionManifest = {
  id: "urp",
  name: "Universal Render Pipeline",
  description: "Universal Render Pipeline extension for Unity. Provides renderer feature management, volume configuration, and pipeline settings through the URP package.",
  version: "1.0.0",
  unityPackages: [
    {
      packageId: "com.unity.render-pipelines.universal",
      displayName: "Universal Render Pipeline"
    }
  ],
  unityInstall: {
    layout: "directory",
    include: ["extension.json", "unitypkg/", "docs/"]
  },
  contributions: {
    tools: [
      { name: "urp.feature", title: "URP Renderer Feature", description: "List, add, remove, toggle or configure URP Renderer Features on the active Universal Render Pipeline asset.", group: "urp", bridgeCommand: "urp.feature", cliCommand: "tools run urp.feature" },
      { name: "urp.volume", title: "URP Volume", description: "List, create or configure URP Volume components and their profile effects.", group: "urp", bridgeCommand: "urp.volume", cliCommand: "tools run urp.volume" },
      { name: "urp.settings", title: "URP Pipeline Settings", description: "Get or set Universal Render Pipeline asset settings (renderScale, supportsHDR, msaaSampleCount, shadowDistance, etc.).", group: "urp", bridgeCommand: "urp.settings", cliCommand: "tools run urp.settings" }
    ],
    contexts: [
      { key: "urp.pipeline", description: "URP render pipeline assets, quality settings, and shader configuration." }
    ],
    bridgeCommands: [
      { group: "urp", commands: ["feature", "volume", "settings"] }
    ],
    csharpEntries: [
      { type: "UnityCli.URP.UrpRendererFeatureTools", description: "Bridge handlers for URP renderer feature operations." },
      { type: "UnityCli.URP.UrpVolumeTools", description: "Bridge handlers for URP volume operations." },
      { type: "UnityCli.URP.UrpSettingsTools", description: "Bridge handlers for URP pipeline settings operations." }
    ],
    docs: [
      { topic: "urp", description: "Universal Render Pipeline: renderer features, volumes, and pipeline settings." }
    ]
  },
  tags: ["rendering", "pipeline", "urp"]
};

export const HDRP_MANIFEST: ExtensionManifest = {
  id: "hdrp",
  name: "High Definition Render Pipeline",
  description: "High Definition Render Pipeline extension for Unity. Provides HDRP volume configuration and pipeline settings through the HDRP package.",
  version: "1.0.0",
  unityPackages: [
    {
      packageId: "com.unity.render-pipelines.high-definition",
      displayName: "High Definition Render Pipeline"
    }
  ],
  unityInstall: {
    layout: "directory",
    include: ["extension.json", "unitypkg/", "docs/"]
  },
  contributions: {
    tools: [
      { name: "hdrp.volume", title: "HDRP Volume", description: "List, create or configure HDRP Volume components and their profile effects.", group: "hdrp", bridgeCommand: "hdrp.volume", cliCommand: "tools run hdrp.volume" },
      { name: "hdrp.settings", title: "HDRP Pipeline Settings", description: "Get or set High Definition Render Pipeline asset settings.", group: "hdrp", bridgeCommand: "hdrp.settings", cliCommand: "tools run hdrp.settings" }
    ],
    contexts: [
      { key: "hdrp.pipeline", description: "HDRP render pipeline assets, volume profiles, and lighting configuration." }
    ],
    bridgeCommands: [
      { group: "hdrp", commands: ["volume", "settings"] }
    ],
    csharpEntries: [
      { type: "UnityCli.HDRP.HdrpVolumeTools", description: "Bridge handlers for HDRP volume operations." },
      { type: "UnityCli.HDRP.HdrpSettingsTools", description: "Bridge handlers for HDRP pipeline settings operations." }
    ],
    docs: [
      { topic: "hdrp", description: "High Definition Render Pipeline: volumes, lighting, and pipeline settings." }
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
