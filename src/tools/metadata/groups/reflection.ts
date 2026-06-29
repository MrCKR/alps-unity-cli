import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Reflection 域：live Unity 反射。对齐 IvanMurzak reflection-method-find/call 与 type-get-json-schema。
 * 与离线 reflect.* 分层：reflect.* 是离线文档/缓存；reflection.* 是 live Unity 反射。
 * reflection.method.call 不做调用范围限制，标高风险，要求 --yes/security 确认。
 */
export const REFLECTION_TOOLS: ToolMetadata[] = [
  {
    name: "reflection.method.find",
    summary: "Find C# methods on a live Unity type.",
    description: "Find C# methods on a live Unity type. No scope restriction: public/private/static/instance methods are all discoverable. Read-only. Use this before reflection.method.call to discover signatures and parameters.",
    group: "reflection",
    inputSchema: objSchema({
      typeName: { type: "string", description: "Full type name, e.g. UnityEngine.Transform." },
      methodName: { type: "string", description: "Optional method name filter." },
      includePrivate: { type: "boolean", default: false, description: "Include non-public methods." },
      includeStatic: { type: "boolean", default: true },
      includeInstance: { type: "boolean", default: true }
    }, ["typeName"]),
    outputSchema: objSchema({
      command: { type: "string" },
      typeName: { type: "string" },
      count: { type: "integer" },
      methods: { type: "array", items: { type: "object" } }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run reflection.method.find",
    docsTopic: "reflection",
    equivalentUnityMcp: "reflection-method-find",
    examples: [
      { description: "Find Translate on Transform", argv: ["tools", "run", "reflection.method.find", "--input", "{\"typeName\":\"UnityEngine.Transform\",\"methodName\":\"Translate\"}"] },
      { description: "List all methods including private", argv: ["tools", "run", "reflection.method.find", "--input", "{\"typeName\":\"UnityEngine.Rigidbody\",\"includePrivate\":true}"] }
    ]
  },
  {
    name: "reflection.method.call",
    summary: "Invoke any C# method via reflection.",
    description: "Invoke any C# method via reflection. No scope restriction: public/private/static/instance, any namespace/assembly, Editor/Runtime. Critical risk: run --dry-run first to preview the target and planned effects, then rerun with --yes only for a known safe method. Use reflection.method.find first to discover signatures.",
    group: "reflection",
    inputSchema: objSchema({
      typeName: { type: "string", description: "Full type name containing the method." },
      methodName: { type: "string", description: "Method name to call." },
      args: { type: "array", description: "Positional arguments array, or object {argName: value}." },
      instanceRef: { type: "object", description: "Instance reference for instance methods: {instanceId} | {assetPath} | {path}." }
    }, ["typeName", "methodName"]),
    outputSchema: objSchema({
      command: { type: "string" },
      typeName: { type: "string" },
      methodName: { type: "string" },
      requiresConfirmation: { type: "boolean" },
      plannedEffects: { type: "array", items: { type: "object" } },
      result: { description: "Serialized result when confirmed execution succeeds." },
      ok: { type: "boolean", description: "Method invocation result flag inside the bridge data." },
      error: { type: "string", description: "Invocation error when the reflected method throws or cannot be called." }
    }),
    destructiveHint: true,
    supportsDryRun: true,
    riskLevel: "critical",
    sideEffects: ["arbitrary-method-invocation"],
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run reflection.method.call",
    docsTopic: "reflection",
    equivalentUnityMcp: "reflection-method-call",
    examples: [
      { description: "Preview a static method call", argv: ["tools", "run", "reflection.method.call", "--dry-run", "--input", "{\"typeName\":\"UnityEngine.Application\",\"methodName\":\"get_unityVersion\"}"] },
      { description: "Execute only after reviewing dry-run", argv: ["tools", "run", "reflection.method.call", "--yes", "--input", "{\"typeName\":\"UnityEngine.Application\",\"methodName\":\"get_unityVersion\"}"] }
    ]
  },
  {
    name: "type.schema",
    summary: "Generate a live JSON Schema for any C# / Unity type, including enum, array, generic, dictionary and $defs references.",
    description: "Generate a live JSON Schema for any C# / Unity type by reflecting over public fields and properties. Handles enum values, arrays/lists, generic type arguments, dictionaries via additionalProperties, and repeated complex types via $defs/$ref. Use before object.modify/component.modify to understand writable structure; keep maxDepth low for large Unity types.",
    group: "type",
    inputSchema: objSchema({
      typeName: { type: "string", description: "Full type name, e.g. UnityEngine.Rigidbody." },
      maxDepth: { type: "integer", default: 3, description: "Max inline schema nesting depth before complex children are truncated or referenced via $defs." }
    }, ["typeName"]),
    outputSchema: objSchema({
      command: { type: "string" },
      typeName: { type: "string" },
      definitionCount: { type: "integer", description: "Number of complex type definitions emitted under schema.$defs." },
      schema: { type: "object", description: "JSON Schema with typeName metadata, enum/array/generic/dictionary shapes, and optional $defs/$ref references." }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run type.schema",
    docsTopic: "reflection",
    equivalentUnityMcp: "type-get-json-schema",
    examples: [
      { description: "Schema for Rigidbody", argv: ["tools", "run", "type.schema", "--input", "{\"typeName\":\"UnityEngine.Rigidbody\"}"] }
    ]
  }
];
