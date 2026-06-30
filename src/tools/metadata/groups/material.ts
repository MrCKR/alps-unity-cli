import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const MATERIAL_TOOLS: ToolMetadata[] = [
  {
    name: "material.info",
    summary: "Read shader, properties and texture references of a Material asset.",
    description: "Read shader, properties and texture references of a Material asset. Use this before material.set to discover valid property names and types. Keep payloads small by passing namePattern for the few properties you need; omit it only for a full material audit. For discovering material paths, use asset.search with filter t:Material instead.",
    group: "material",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.mat path." },
      namePattern: { type: "string", description: "Optional comma-separated property names to return (e.g. _Color,_MainTex). Omit to return all shader properties." }
    }, ["path"]),
    outputSchema: objSchema({
      command: { type: "string" },
      material: objSchema({
        path: { type: "string" },
        guid: { type: "string" },
        name: { type: "string" },
        shader: { type: "string" },
        renderQueue: { type: "integer" },
        keywords: { type: "array", items: { type: "string" } },
        properties: objSchema({
          count: { type: "integer" },
          items: { type: "array", items: objSchema({ name: { type: "string" }, displayName: { type: "string" }, type: { type: "string" } }) }
        }),
        textureReferences: objSchema({
          count: { type: "integer" },
          items: { type: "array", items: objSchema({ property: { type: "string" }, path: { type: "string" }, guid: { type: "string" } }) }
        })
      }),
      nameFilterApplied: { type: "boolean", description: "True when namePattern filtered the returned properties." }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read only specific properties (token-safe)", argv: ["tools", "run", "material.info", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\",\"namePattern\":\"_Color,_MainTex\"}"] },
      { description: "Full material audit", argv: ["tools", "run", "material.info", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\"}"] }
    ]
  },
  {
    name: "material.create",
    summary: "Create a new Material asset from a named shader at an Assets/*.mat path.",
    description: "Create a new Material asset from a named shader at an Assets/*.mat path. Use shader.list or asset.search-built-in to discover a valid shader name before calling; an unknown shader returns E_SHADER_NOT_FOUND. Refuses to overwrite an existing material unless overwrite is true.",
    group: "material",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/*.mat path. Intermediate folders are created." },
      shader: { type: "string", description: "Unity shader name resolvable by Shader.Find, e.g. Standard or Universal Render Pipeline/Lit." },
      overwrite: { type: "boolean", description: "Allow replacing an existing material at path." }
    }, ["path", "shader"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Create a fixture material", argv: ["tools", "run", "material.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedMaterial.mat\",\"shader\":\"Standard\"}"] }
    ]
  },
  {
    name: "material.assign",
    summary: "Assign a Material asset to a GameObject renderer or UI Graphic. target resolves by name, hierarchy path or instance id; prefer path/id when names are ambiguous.",
    description: "Assign a Material asset to a GameObject renderer or UI Graphic. target resolves by name, hierarchy path or instance id; prefer path/id when names are ambiguous. This writes sharedMaterials on Renderers, so the assignment affects every object sharing that material slot. Use component.list-types or go.inspect first when the target components are unknown.",
    group: "material",
    inputSchema: objSchema({
      target: { type: "string", description: "Target GameObject: name, hierarchy path (e.g. Parent/Child) or instance id." },
      material: { type: "string", description: "Existing Assets/*.mat path to assign." },
      slot: { type: "integer", default: 0, description: "Renderer material slot index (0-based). Validated against the renderer's material count." }
    }, ["target", "material"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Assign a material to a fixture object's slot 0", argv: ["tools", "run", "material.assign", "--input", "{\"target\":\"AlignmentProbe\",\"material\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\",\"slot\":0}"] }
    ]
  },
  {
    name: "material.set",
    summary: "Set a named shader property on a Material asset (color, float, vector or texture).",
    description: "Set a named shader property on a Material asset (color, float, vector or texture). Inspect valid property names and types with material.info first; a wrong type returns E_MATERIAL_PROPERTY_TYPE_MISMATCH. This edits the shared asset, so changes apply to all objects using the material. Color accepts #RRGGBB, #RRGGBBAA or comma-separated r,g,b[,a]; texture accepts an Assets/* path or 'null' to clear.",
    group: "material",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.mat path." },
      property: { type: "string", description: "Shader property name, e.g. _Color or _MainTex." },
      value: { type: "string", description: "Property value: #hex / r,g,b[,a] for color, number for float, x,y,z,w for vector, Assets/* texture path or null." },
      color: { type: "string", description: "Typed color value alias for value when type=color." },
      floatValue: { type: "number", description: "Typed numeric value alias for value when type=float." },
      vector: { type: "string", description: "Typed vector value alias for value when type=vector, formatted x,y,z,w." },
      texture: { type: "string", description: "Typed texture path alias for value when type=texture; use null to clear." },
      type: { type: "string", enum: ["color", "float", "vector", "texture"], description: "Force the value type. When omitted it is inferred from the shader property." }
    }, ["path", "property"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Set a color property", argv: ["tools", "run", "material.set", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\",\"property\":\"_Color\",\"value\":\"#FF0000\",\"type\":\"color\"}"] },
      { description: "Set a float property", argv: ["tools", "run", "material.set", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\",\"property\":\"_Metallic\",\"floatValue\":0.8,\"type\":\"float\"}"] }
    ]
  },
  {
    name: "material.set-renderer-color",
    summary: "Set a Renderer instance color with MaterialPropertyBlock without modifying the shared Material asset.",
    description: "Set a Renderer instance color with MaterialPropertyBlock without modifying the shared Material asset. Use this for scene/session color changes on one GameObject; use material.set when you intend to edit the Material asset shared by all users.",
    group: "material",
    inputSchema: objSchema({ target: { type: "string", description: "Target GameObject with a Renderer: name, hierarchy path, or instance id." }, property: { type: "string", default: "_Color", description: "Shader color property name to set on the property block." }, color: { type: "string", description: "#RRGGBB, #RRGGBBAA, or comma-separated r,g,b[,a]." }, value: { type: "string", description: "Alias of color." }, slot: { type: "integer", description: "Optional material slot index for per-slot property block." } }, ["target", "color"]),
    outputSchema: objSchema({ target: { type: "object" }, property: { type: "string" }, color: { type: "object" }, slot: { type: ["integer", "null"] }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    sideEffects: ["scene-modify", "renderer-property-block"],
    playModeSupport: "session-affecting",
    supportsDryRun: true,
    examples: [
      { description: "Set one renderer color without editing the material asset", argv: ["tools", "run", "material.set-renderer-color", "--input", "{\"target\":\"Player\",\"color\":\"#ff0000\"}"] }
    ]
  }
];
