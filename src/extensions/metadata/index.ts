/**
 * Static built-in extension metadata for the Unity CLI.
 *
 * P1 baseline: skeleton registry with extension descriptors for
 * ProBuilder, Cinemachine, VFX Graph, URP, HDRP.
 * P5 will implement install/remove/init-detection full flow.
 *
 * Extensions are optional capability packs that depend on optional
 * Unity packages. They provide tools and contexts but must not
 * bypass the descriptor/schema/envelope/target/availability system.
 */

import type { ExtensionMetadata } from "./types.js";

export type { ExtensionMetadata } from "./types.js";

// ── ProBuilder Extension ──

const PROBUILDER_EXTENSION: ExtensionMetadata = {
  id: "probuilder",
  title: "ProBuilder Extension",
  description: "ProBuilder 3D modeling tools: create shapes, CSG operations, mesh editing.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: [
    "probuilder.ping", "probuilder.create-shape", "probuilder.create-poly-shape",
    "probuilder.extrude-faces", "probuilder.delete-faces", "probuilder.detach-faces",
    "probuilder.merge-faces", "probuilder.subdivide", "probuilder.flip-normals",
    "probuilder.select-faces", "probuilder.set-face-material", "probuilder.set-face-color",
    "probuilder.set-face-uvs", "probuilder.extrude-edges", "probuilder.bevel-edges",
    "probuilder.bridge-edges", "probuilder.connect-elements", "probuilder.combine-meshes",
    "probuilder.merge-objects", "probuilder.duplicate-and-flip", "probuilder.create-polygon",
    "probuilder.merge-vertices", "probuilder.weld-vertices", "probuilder.split-vertices",
    "probuilder.move-vertices", "probuilder.insert-vertex", "probuilder.append-vertices-to-edge",
    "probuilder.mesh-info", "probuilder.validate-mesh", "probuilder.convert-to-probuilder",
    "probuilder.set-smoothing", "probuilder.auto-smooth", "probuilder.center-pivot",
    "probuilder.freeze-transform", "probuilder.set-pivot", "probuilder.repair-mesh"
  ],
  requiresUnityPackages: ["com.unity.probuilder"],
  installMode: "auto",
  installCommand: "extensions install probuilder",
  equivalentMcpGroup: "probuilder",
  nextActions: [
    "Run unity_cli extensions install probuilder to install this extension.",
    "Run unity_cli init to auto-detect and install extensions for your Unity project."
  ]
};

// ── Cinemachine Extension ──

const CINEMACHINE_EXTENSION: ExtensionMetadata = {
  id: "cinemachine",
  title: "Cinemachine Extension",
  description: "Cinemachine camera system tools: target, lens, priority, blend, body, aim, noise, brain.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: [],
  requiresUnityPackages: ["com.unity.cinemachine"],
  installMode: "auto",
  installCommand: "extensions install cinemachine",
  equivalentMcpGroup: "cinemachine",
  nextActions: [
    "Run unity_cli extensions install cinemachine to install this extension.",
    "Run unity_cli init to auto-detect and install extensions for your Unity project."
  ]
};

// ── VFX Graph Extension ──

const VFX_GRAPH_EXTENSION: ExtensionMetadata = {
  id: "vfx-graph",
  title: "VFX Graph Extension",
  description: "VFX Graph advanced visual effect tools: graph parameters, assets, templates, events.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: ["vfx.graph", "vfx.effect"],
  requiresUnityPackages: ["com.unity.visualeffectgraph"],
  installMode: "auto",
  installCommand: "extensions install vfx-graph",
  equivalentMcpGroup: "vfx",
  nextActions: [
    "Run unity_cli extensions install vfx-graph to install this extension.",
    "Run unity_cli init to auto-detect and install extensions for your Unity project."
  ]
};

// ── URP Extension ──

const URP_EXTENSION: ExtensionMetadata = {
  id: "urp",
  title: "URP Extension",
  description: "Universal Render Pipeline tools: renderer features, URP-specific volume and rendering.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: [],
  providesContexts: ["scene.renderer-features"],
  requiresUnityPackages: ["com.unity.render-pipelines.universal"],
  installMode: "auto",
  installCommand: "extensions install urp",
  nextActions: [
    "Run unity_cli extensions install urp to install this extension.",
    "Run unity_cli init to auto-detect and install extensions for your Unity project."
  ]
};

// ── HDRP Extension ──

const HDRP_EXTENSION: ExtensionMetadata = {
  id: "hdrp",
  title: "HDRP Extension",
  description: "High Definition Render Pipeline tools: HDRP volume, rendering, and pipeline-specific features.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: [],
  requiresUnityPackages: ["com.unity.render-pipelines.high-definition"],
  installMode: "auto",
  installCommand: "extensions install hdrp",
  nextActions: [
    "Run unity_cli extensions install hdrp to install this extension.",
    "Run unity_cli init to auto-detect and install extensions for your Unity project."
  ]
};

// ── All built-in extensions ──

const BUILTIN_EXTENSIONS: ExtensionMetadata[] = [
  PROBUILDER_EXTENSION,
  CINEMACHINE_EXTENSION,
  VFX_GRAPH_EXTENSION,
  URP_EXTENSION,
  HDRP_EXTENSION
];

/** Return all built-in extension metadata entries. */
export function builtinExtensionMetadata(): ExtensionMetadata[] {
  return BUILTIN_EXTENSIONS;
}

/** Find a single built-in extension by its id. */
export function findExtensionById(id: string): ExtensionMetadata | undefined {
  return BUILTIN_EXTENSIONS.find((e) => e.id === id);
}

/** Search extensions by keyword across id, title, description. */
export function searchExtensions(keyword: string): ExtensionMetadata[] {
  const lower = keyword.toLowerCase();
  return BUILTIN_EXTENSIONS.filter((e) =>
    e.id.toLowerCase().includes(lower) ||
    e.title.toLowerCase().includes(lower) ||
    e.description.toLowerCase().includes(lower)
  );
}
