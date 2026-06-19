/**
 * Initial registry data for the five built-in CLI extensions.
 *
 * ProBuilder and VFX Graph have existing bridge domains and tool metadata;
 * Cinemachine, URP, and HDRP are registered as future extensions whose
 * tools will be implemented in P7/P8.
 */

import type { ExtensionManifest } from "./types.js";

export const PROBUILDER_MANIFEST: ExtensionManifest = {
  id: "probuilder",
  name: "ProBuilder",
  description: "3D modeling and level design tools for Unity. Provides shape creation, mesh editing, and boolean CSG operations through the ProBuilder package.",
  version: "1.0.0",
  unityPackages: [
    {
      packageId: "com.unity.probuilder",
      displayName: "ProBuilder"
    }
  ],
  contributions: {
    tools: [
      { name: "probuilder.shape", title: "ProBuilder Shape", description: "Create a ProBuilder primitive shape in the scene.", group: "probuilder", bridgeCommand: "probuilder.shape", cliCommand: "probuilder shape" },
      { name: "probuilder.create", title: "ProBuilder Create", description: "Create a ProBuilder mesh with a specified shape.", group: "probuilder", bridgeCommand: "probuilder.create", cliCommand: "probuilder create" },
      { name: "probuilder.edit", title: "ProBuilder Edit", description: "Edit a ProBuilder mesh element with an operation.", group: "probuilder", bridgeCommand: "probuilder.edit", cliCommand: "probuilder edit" },
      { name: "probuilder.csg", title: "ProBuilder CSG", description: "Boolean CSG operation between two ProBuilder meshes.", group: "probuilder", bridgeCommand: "probuilder.csg", cliCommand: "probuilder csg" }
    ],
    contexts: [
      { key: "probuilder.scene", description: "ProBuilder mesh elements and operations in the current scene." }
    ],
    bridgeCommands: [
      { group: "probuilder", commands: ["shape", "create", "edit", "csg"] }
    ],
    csharpEntries: [
      { type: "UnityCli.ProBuilderCommands", description: "Bridge handler for ProBuilder shape creation, mesh editing, and CSG operations." }
    ],
    docs: [
      { topic: "probuilder", description: "ProBuilder modeling tools: shape primitives, mesh editing, and boolean CSG." }
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
