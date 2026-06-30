import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const SHADER_TOOLS: ToolMetadata[] = [
  {
    name: "shader.info",
    summary: "Read metadata of a shader asset: name, asset type, ShaderGraph flag, file size, and compilation diagnostics (supported flag + compiler messages).",
    description: "Read metadata of a shader asset: name, asset type, ShaderGraph flag, file size, and compilation diagnostics (supported flag + compiler messages). Use shader.list to discover the path when unknown. For full property/passes detail use shader.get-data instead.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.shader or Assets/*.shadergraph path." } }, ["path"]),
    outputSchema: objSchema({
      shader: objSchema({
        path: { type: "string" },
        guid: { type: "string" },
        name: { type: "string" },
        assetType: { type: "string" },
        isShaderGraph: { type: "boolean" },
        exists: { type: "boolean" },
        sizeBytes: { type: "integer" },
        diagnostics: objSchema({ supported: { type: "boolean" }, messageCount: { type: "integer" } })
      })
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Inspect a shader asset and its compile diagnostics", argv: ["tools", "run", "shader.info", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeShader.shader\"}"] }
    ]
  },
  {
    name: "shader.read",
    summary: "Read the UTF-8 source code of a .shader file.",
    description: "Read the UTF-8 source code of a .shader file. Use this to inspect or port HLSL before shader.update. ShaderGraph (.shadergraph) assets are binary JSON and not meant for line editing; prefer shader.info for those.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.shader path." } }, ["path"]),
    outputSchema: objSchema({
      shader: objSchema({ path: { type: "string" }, guid: { type: "string" }, name: { type: "string" }, encoding: { type: "string" } }),
      content: { type: "string" },
      encoding: { type: "string" }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read shader source for editing", argv: ["tools", "run", "shader.read", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeShader.shader\"}"] }
    ]
  },
  {
    name: "shader.create",
    summary: "Create a new .shader asset.",
    description: "Create a new .shader asset. When source is omitted, a compilable default unlit/vertex-color shader is generated. ShaderGraph (.shadergraph) creation requires explicit source via content/content-file. Refuses to overwrite an existing file unless overwrite is true. Use shader.list first to pick a non-conflicting path.",
    group: "shader",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/*.shader or Assets/*.shadergraph path." },
      source: { type: "string", description: "Shader source text. Omit for a generated default .shader template." },
      content: { type: "string", description: "Alias of source; also accepts inline shader text." },
      name: { type: "string", description: "Optional internal Shader name for the default template." },
      overwrite: { type: "boolean", description: "Allow replacing an existing shader file." }
    }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Create a default shader", argv: ["tools", "run", "shader.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedProbe.shader\"}"] }
    ]
  },
  {
    name: "shader.update",
    summary: "Replace the source of an existing .shader asset and force a reimport, which recompiles the shader.",
    description: "Replace the source of an existing .shader asset and force a reimport, which recompiles the shader. This can surface compile errors visible via shader.info diagnostics; inspect them after updating. Prefer shader.read first to capture the current source as a precondition.",
    group: "shader",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.shader path." },
      source: { type: "string", description: "Replacement shader source text." },
      content: { type: "string", description: "Alias of source; inline replacement text." }
    }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import", "shader-recompile"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Replace shader source and recompile", argv: ["tools", "run", "shader.update", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeShader.shader\",\"content\":\"Shader \\\"UnityCli/Probe\\\" { SubShader { Pass {} } }\"}"] }
    ]
  },
  {
    name: "shader.delete",
    summary: "Delete an existing Shader or ShaderGraph asset through a shader-specific destructive tool.",
    description: "Delete an existing Shader or ShaderGraph asset through a shader-specific destructive tool. This is equivalent in effect to asset.delete for shader files, but is discoverable from the shader group. Use --dry-run before --yes when the path was discovered programmatically.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.shader or Assets/*.shadergraph path to delete." } }, ["path"]),
    outputSchema: objSchema({ shader: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["asset-delete", "asset-import"],
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview deleting a generated shader", argv: ["tools", "run", "shader.delete", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedProbe.shader\"}", "--dry-run"] },
      { description: "Delete a generated shader after confirmation", argv: ["tools", "run", "shader.delete", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedProbe.shader\"}", "--yes"] }
    ]
  },
  {
    name: "shader.list",
    summary: "List shader assets in the project with name, path, guid, property count and render queue.",
    description: "List shader assets in the project with name, path, guid, property count and render queue. Keep pageSize small; use this to discover a valid shader name for material.create. Results are capped by pageSize (clamped 1..500).",
    group: "shader",
    inputSchema: objSchema({
      keyword: { type: "string", description: "Optional AssetDatabase search keyword." },
      pageSize: { type: "integer", default: 100, description: "Maximum shaders returned, clamped to 1..500." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      count: { type: "integer" },
      shaders: { type: "array", items: objSchema({ path: { type: "string" }, guid: { type: "string" }, name: { type: "string" }, propertyCount: { type: "integer" }, renderQueue: { type: "integer" } }) }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List shaders to find a name for material.create", argv: ["tools", "run", "shader.list", "--input", "{\"keyword\":\"Standard\",\"pageSize\":25}"] }
    ]
  },
  {
    name: "shader.get-data",
    summary: "Read shader properties, diagnostics, SubShader and Pass structure with token-safe include switches.",
    description: "Read shader properties, diagnostics, SubShader and Pass structure with token-safe include switches. Accepts an Assets/* path or a shader name resolved via Shader.Find. Defaults include compile messages, properties and capped SubShader/Pass structure; source code is omitted unless includeSourceCode=true. Use shader.list or asset.search with filter t:Shader first to locate the path. This returns property definitions, not material instance values; for material values use material.info.",
    group: "shader",
    inputSchema: objSchema({
      path: { type: "string", description: "Exact Assets/* shader path." },
      assetPath: { type: "string", description: "Alias of path." },
      name: { type: "string", description: "Shader name resolved via Shader.Find when no path is supplied." },
      includeMessages: { type: "boolean", default: true, description: "Include ShaderUtil compile messages in diagnostics.messages." },
      includeProperties: { type: "boolean", default: true, description: "Include shader property definitions." },
      includeSubshaders: { type: "boolean", default: true, description: "Include SubShader and Pass structure, capped by maxSubshaders/maxPasses." },
      includeSourceCode: { type: "boolean", default: false, description: "Include pass sourceCode when Unity exposes it. Can be large." },
      maxSubshaders: { type: "integer", default: 8, description: "Maximum SubShaders returned, clamped 0..64." },
      maxPasses: { type: "integer", default: 16, description: "Maximum Passes per SubShader returned, clamped 0..128." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      name: { type: "string" },
      renderQueue: { type: "integer" },
      isSupported: { type: "boolean" },
      passCount: { type: "integer" },
      propertyCount: { type: "integer" },
      diagnostics: { type: "object" },
      properties: { type: "array", items: objSchema({ name: { type: "string" }, description: { type: "string" }, type: { type: "string" }, flags: { type: "string" }, nameId: { type: "integer" } }) },
      subshaderDataAvailable: { type: "boolean" },
      subshaders: { type: "array", items: objSchema({ index: { type: "integer" }, passCount: { type: "integer" }, passes: { type: "array", items: objSchema({ index: { type: "integer" }, name: { type: "string" }, sourceCode: { type: "string" } }) } }) }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read shader property definitions and pass structure by path", argv: ["tools", "run", "shader.get-data", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeShader.shader\",\"includeSubshaders\":true,\"maxPasses\":8}"] },
      { description: "Resolve a built-in shader by name without source code", argv: ["tools", "run", "shader.get-data", "--input", "{\"name\":\"Standard\",\"includeSourceCode\":false}"] }
    ]
  }
];
