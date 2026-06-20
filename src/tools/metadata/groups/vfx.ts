import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * VFX Graph extension tools.
 *
 * Critical reference notes (ALP-505 P8):
 * - Critically references unity-mcp's Vfx/ManageVFX.cs (action router), VfxGraphRead.cs,
 *   VfxGraphWrite.cs, VfxGraphControl.cs, VfxGraphAssets.cs for the action decomposition
 *   and Unity API paths.
 * - Discarded: unity-mcp uses a single manage_vfx tool with action=... routing for both
 *   ParticleSystem and VFX Graph. alps-unity-cli keeps "multi-tool single-action" and only
 *   VFX Graph capabilities go under the vfx group; ParticleSystem belongs elsewhere.
 * - Discarded: unity-mcp uses #if UNITY_VFX_GRAPH / #else conditional compilation for VFX
 *   Graph code. alps-unity-cli uses runtime FindType() + PackageManifestContains detection
 *   (no conditional compile symbols) matching ProBuilder's pattern.
 * - Adapted: VFX parameter setting uses reflection-based SetFloat/SetVector3/etc instead of
 *   direct VisualEffect component API calls, since we have no compile-time dependency.
 */
export const VFX_TOOLS: ToolMetadata[] = [
  {
    name: "vfx.graph",
    title: "VFX Graph",
    description: "Get info on a Visual Effect graph asset, optionally create one. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({ path: { type: "string" }, action: { type: "string", enum: ["info", "create"] }, create: { type: "boolean" }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx graph",
    docsTopic: "vfx",
    requiresPackages: ["com.unity.visualeffectgraph"]
  },
  {
    name: "vfx.effect",
    title: "VFX Effect",
    description: "Manage a Visual Effect instance on a GameObject. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({ target: { type: "string" }, graph: { type: "string" }, add: { type: "boolean" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx effect",
    docsTopic: "vfx",
    requiresPackages: ["com.unity.visualeffectgraph"]
  },
  {
    name: "vfx.parameter",
    title: "VFX Parameter",
    description: "Set exposed parameters on a Visual Effect instance (float, int, bool, vector, color, texture, mesh, gradient, animation curve). Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      name: { type: "string" },
      type: { type: "string", enum: ["float", "int", "bool", "vector2", "vector3", "vector4", "color", "texture", "mesh", "gradient", "curve"] },
      value: { type: "string" },
      x: { type: "number" },
      y: { type: "number" },
      z: { type: "number" },
      w: { type: "number" },
      r: { type: "number" },
      g: { type: "number" },
      b: { type: "number" },
      a: { type: "number" }
    }, ["target", "name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx parameter",
    docsTopic: "vfx",
    requiresPackages: ["com.unity.visualeffectgraph"],
    examples: [
      { description: "Set an exposed float parameter", argv: ["vfx", "parameter", "VFXFire", "--name", "Intensity", "--type", "float", "--value", "2.5"] },
      { description: "Set a vector3 parameter", argv: ["vfx", "parameter", "VFXFire", "--name", "Velocity", "--type", "vector3", "--x", "0", "--y", "1", "--z", "0"] }
    ]
  },
  {
    name: "vfx.control",
    title: "VFX Control",
    description: "Control playback of a Visual Effect instance (play, stop, pause, reinit, set play rate, set seed). Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      action: { type: "string", enum: ["play", "stop", "pause", "reinit", "rate", "seed"] },
      rate: { type: "number" },
      seed: { type: "integer" },
      resetSeedOnPlay: { type: "boolean" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx control",
    docsTopic: "vfx",
    requiresPackages: ["com.unity.visualeffectgraph"],
    examples: [
      { description: "Play a VFX effect", argv: ["vfx", "control", "VFXFire", "--action", "play"] },
      { description: "Set playback rate to 0.5x", argv: ["vfx", "control", "VFXFire", "--action", "rate", "--rate", "0.5"] }
    ]
  },
  {
    name: "vfx.event",
    title: "VFX Event",
    description: "Send a named event to a Visual Effect instance, optionally with attribute payloads (position, velocity, size, etc.). Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({
      target: { type: "string" },
      eventName: { type: "string" },
      attributeName: { type: "string" },
      attributeX: { type: "number" },
      attributeY: { type: "number" },
      attributeZ: { type: "number" },
      attributeSize: { type: "number" }
    }, ["target", "eventName"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx event",
    docsTopic: "vfx",
    requiresPackages: ["com.unity.visualeffectgraph"],
    examples: [
      { description: "Send an Explode event with position attribute", argv: ["vfx", "event", "VFXFire", "--event-name", "Explode", "--attribute-name", "position", "--attribute-x", "1", "--attribute-y", "2", "--attribute-z", "3"] }
    ]
  },
  {
    name: "vfx.template",
    title: "VFX Template",
    description: "Create a Visual Effect Graph asset from a built-in VFX Graph template. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({
      path: { type: "string" },
      template: { type: "string" },
      overwrite: { type: "boolean" }
    }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx template",
    docsTopic: "vfx",
    requiresPackages: ["com.unity.visualeffectgraph"],
    examples: [
      { description: "Create from AR template", argv: ["vfx", "template", "Assets/VFX/Sparkle.vfx", "--template", "AR/AugmentedRealityTemplate"] }
    ]
  }
];
