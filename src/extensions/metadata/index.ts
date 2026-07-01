/**
 * Static built-in extension metadata for the Unity CLI.
 *
 * Built-in extension descriptors for ProBuilder, Cinemachine, VFX Graph, URP, HDRP.
 *
 * Extensions are optional capability packs that depend on optional
 * Unity packages. They provide tools and contexts but must not
 * bypass the descriptor/schema/envelope/target/availability system.
 */

import { CINEMACHINE_TOOLS } from "../../tools/metadata/groups/cinemachine.js";
import { HDRP_TOOLS } from "../../tools/metadata/groups/hdrp.js";
import { PROBUILDER_TOOLS } from "../../tools/metadata/groups/probuilder.js";
import { URP_TOOLS } from "../../tools/metadata/groups/urp.js";
import { VFX_TOOLS } from "../../tools/metadata/groups/vfx.js";
import type { ExtensionMetadata } from "./types.js";

export type { ExtensionMetadata } from "./types.js";

// ── ProBuilder Extension ──

const PROBUILDER_EXTENSION: ExtensionMetadata = {
  id: "probuilder",
  title: "ProBuilder Extension",
  description: "ProBuilder 3D modeling tools: shape creation, mesh editing, face/edge/vertex operations, smoothing, pivots, and repair.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: PROBUILDER_TOOLS.map((tool) => tool.name),
  requiresUnityPackages: ["com.unity.probuilder"],
  installMode: "auto",
  installCommand: "extensions install probuilder",
  nextActions: [
    "Run unity_cli extensions install probuilder to install this extension.",
    "Run unity_cli init to auto-detect and install extensions for your Unity project."
  ]
};

// ── Cinemachine Extension ──

const CINEMACHINE_EXTENSION: ExtensionMetadata = {
  id: "cinemachine",
  title: "Cinemachine Extension",
  description: "Cinemachine camera tools: virtual camera creation, targets, lens, priority, blends, body/aim/noise, brain, overrides, and extensions.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: CINEMACHINE_TOOLS.map((tool) => tool.name),
  requiresUnityPackages: ["com.unity.cinemachine"],
  installMode: "auto",
  installCommand: "extensions install cinemachine",
  nextActions: [
    "Run unity_cli extensions install cinemachine to install this extension.",
    "Run unity_cli init to auto-detect and install extensions for your Unity project."
  ]
};

// ── VFX Graph Extension ──

const VFX_GRAPH_EXTENSION: ExtensionMetadata = {
  id: "vfx-graph",
  title: "VFX Extension",
  description: "Visual effects tools: VFX Graph assets/instances, parameters, events, ParticleSystem modules, LineRenderer, and TrailRenderer.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: VFX_TOOLS.map((tool) => tool.name),
  requiresUnityPackages: [],
  installMode: "auto",
  installCommand: "extensions install vfx-graph",
  nextActions: [
    "Run unity_cli extensions install vfx-graph to install this extension.",
    "Run unity_cli init to auto-detect and install extensions for your Unity project."
  ]
};

// ── URP Extension ──

const URP_EXTENSION: ExtensionMetadata = {
  id: "urp",
  title: "URP Extension",
  description: "URP rendering tools: renderer features, volume components, and Universal Render Pipeline asset settings.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: URP_TOOLS.map((tool) => tool.name),
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
  description: "HDRP rendering tools: volume components and High Definition Render Pipeline asset settings.",
  kind: "extension",
  source: "built-in",
  version: "1.0.0",
  status: "available",
  providesTools: HDRP_TOOLS.map((tool) => tool.name),
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
