/**
 * Initial registry data for the five built-in CLI extensions.
 *
 * ProBuilder has 36 fine-grained tool contributions.
 * Cinemachine has 13 camera tools.
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
      { name: "probuilder.ping" },
      { name: "probuilder.create-shape" },
      { name: "probuilder.create-poly-shape" },
      { name: "probuilder.extrude-faces" },
      { name: "probuilder.delete-faces" },
      { name: "probuilder.detach-faces" },
      { name: "probuilder.merge-faces" },
      { name: "probuilder.subdivide" },
      { name: "probuilder.flip-normals" },
      { name: "probuilder.select-faces" },
      { name: "probuilder.set-face-material" },
      { name: "probuilder.set-face-color" },
      { name: "probuilder.set-face-uvs" },
      { name: "probuilder.extrude-edges" },
      { name: "probuilder.bevel-edges" },
      { name: "probuilder.bridge-edges" },
      { name: "probuilder.connect-elements" },
      { name: "probuilder.combine-meshes" },
      { name: "probuilder.merge-objects" },
      { name: "probuilder.duplicate-and-flip" },
      { name: "probuilder.create-polygon" },
      { name: "probuilder.merge-vertices" },
      { name: "probuilder.weld-vertices" },
      { name: "probuilder.split-vertices" },
      { name: "probuilder.move-vertices" },
      { name: "probuilder.insert-vertex" },
      { name: "probuilder.append-vertices-to-edge" },
      { name: "probuilder.mesh-info" },
      { name: "probuilder.validate-mesh" },
      { name: "probuilder.convert-to-probuilder" },
      { name: "probuilder.set-smoothing" },
      { name: "probuilder.auto-smooth" },
      { name: "probuilder.center-pivot" },
      { name: "probuilder.freeze-transform" },
      { name: "probuilder.set-pivot" },
      { name: "probuilder.repair-mesh" }
    ],
    contexts: [
      { key: "probuilder.scene", description: "ProBuilder mesh elements and operations in the current scene." }
    ],
    csharpEntries: [
      { type: "UnityCli.ProBuilder.ProBuilderUtilityTools", description: "Bridge handlers for ProBuilder ping, mesh-info, and validate-mesh." },
      { type: "UnityCli.ProBuilder.ProBuilderShapeTools", description: "Bridge handlers for ProBuilder create-shape and create-poly-shape." },
      { type: "UnityCli.ProBuilder.ProBuilderMeshEditTools", description: "Bridge handlers for ProBuilder face, edge, and mesh editing operations." },
      { type: "UnityCli.ProBuilder.ProBuilderVertexTools", description: "Bridge handlers for ProBuilder vertex operations." },
      { type: "UnityCli.ProBuilder.ProBuilderSelectionTools", description: "Bridge handlers for ProBuilder selection and material/color/UV operations." },
      { type: "UnityCli.ProBuilder.ProBuilderSmoothingTools", description: "Bridge handlers for ProBuilder smoothing, pivot, transform, and repair operations." }
    ],
  },
  tags: ["modeling", "3d", "level-design"]
};

export const VFX_GRAPH_MANIFEST: ExtensionManifest = {
  id: "vfx-graph",
  name: "VFX Graph",
  description: "Visual effects extension with VFX Graph package tools plus built-in ParticleSystem, LineRenderer, and TrailRenderer tools.",
  version: "1.0.0",
  unityPackages: [],
  unityInstall: {
    layout: "directory",
    include: ["extension.json", "unitypkg/", "docs/"]
  },
  contributions: {
    tools: [
      { name: "vfx.graph", requiresPackages: ["com.unity.visualeffectgraph"] },
      { name: "vfx.effect", requiresPackages: ["com.unity.visualeffectgraph"] },
      { name: "vfx.parameter", requiresPackages: ["com.unity.visualeffectgraph"] },
      { name: "vfx.control", requiresPackages: ["com.unity.visualeffectgraph"] },
      { name: "vfx.event", requiresPackages: ["com.unity.visualeffectgraph"] },
      { name: "vfx.template", requiresPackages: ["com.unity.visualeffectgraph"] },
      { name: "vfx.particle-create", requiresPackages: [] },
      { name: "vfx.particle-info", requiresPackages: [] },
      { name: "vfx.particle-main", requiresPackages: [] },
      { name: "vfx.particle-emission", requiresPackages: [] },
      { name: "vfx.particle-shape", requiresPackages: [] },
      { name: "vfx.particle-play", requiresPackages: [] },
      { name: "vfx.particle-module", requiresPackages: [] },
      { name: "vfx.line-create", requiresPackages: [] },
      { name: "vfx.line-positions", requiresPackages: [] },
      { name: "vfx.trail", requiresPackages: [] }
    ],
    contexts: [],
    csharpEntries: [
      { type: "UnityCli.VfxCommands", description: "Bridge handler for VFX Graph, ParticleSystem, LineRenderer, and TrailRenderer operations." }
    ],
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
      { name: "cinemachine.ping" },
      { name: "cinemachine.create" },
      { name: "cinemachine.target" },
      { name: "cinemachine.lens" },
      { name: "cinemachine.priority" },
      { name: "cinemachine.blend" },
      { name: "cinemachine.body" },
      { name: "cinemachine.aim" },
      { name: "cinemachine.noise" },
      { name: "cinemachine.brain" },
      { name: "cinemachine.force-camera" },
      { name: "cinemachine.release-override" },
      { name: "cinemachine.extension" }
    ],
    contexts: [
      { key: "cinemachine.cameras", description: "Cinemachine virtual cameras and composition in the current scene." }
    ],
    csharpEntries: [
      { type: "UnityCli.Cinemachine.CinemachineCameraTools", description: "Bridge handlers for Cinemachine ping, create, target, lens, priority, blend, body, aim, noise, brain, force-camera, release-override, and extension operations." }
    ],
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
      { name: "urp.feature" },
      { name: "urp.volume" },
      { name: "urp.settings" }
    ],
    contexts: [
      { key: "urp.pipeline", description: "URP render pipeline assets, quality settings, and shader configuration." }
    ],
    csharpEntries: [
      { type: "UnityCli.URP.UrpRendererFeatureTools", description: "Bridge handlers for URP renderer feature operations." },
      { type: "UnityCli.URP.UrpVolumeTools", description: "Bridge handlers for URP volume operations." },
      { type: "UnityCli.URP.UrpSettingsTools", description: "Bridge handlers for URP pipeline settings operations." }
    ],
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
      { name: "hdrp.volume" },
      { name: "hdrp.settings" }
    ],
    contexts: [
      { key: "hdrp.pipeline", description: "HDRP render pipeline assets, volume profiles, and lighting configuration." }
    ],
    csharpEntries: [
      { type: "UnityCli.HDRP.HdrpVolumeTools", description: "Bridge handlers for HDRP volume operations." },
      { type: "UnityCli.HDRP.HdrpSettingsTools", description: "Bridge handlers for HDRP pipeline settings operations." }
    ],
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
