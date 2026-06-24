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
    title: "Find Methods",
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
    title: "Call Method",
    description: "Invoke any C# method via reflection. No scope restriction: public/private/static/instance, any namespace/assembly, Editor/Runtime. High-risk: requires security confirmation (--yes with destructiveFingerprint). Returns target method preview, structured audit, exceptions and result serialization.",
    group: "reflection",
    inputSchema: objSchema({
      typeName: { type: "string", description: "Full type name containing the method." },
      methodName: { type: "string", description: "Method name to call." },
      args: { type: "array", description: "Positional arguments array, or object {argName: value}." },
      instanceRef: { type: "object", description: "Instance reference for instance methods: {instanceId} | {assetPath} | {path}." },
      security: { type: "object", description: "Confirmation: {confirmed: true, destructiveFingerprint: \"...\"}." }
    }, ["typeName", "methodName"]),
    destructiveHint: true,
    supportsDryRun: false,
    riskLevel: "critical",
    sideEffects: ["arbitrary-method-invocation"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run reflection.method.call",
    docsTopic: "reflection",
    equivalentUnityMcp: "reflection-method-call",
    examples: [
      { description: "Static method (requires confirmation)", argv: ["tools", "run", "reflection.method.call", "--input", "{\"typeName\":\"UnityEngine.Physics\",\"methodName\":\"Gravity\",\"security\":{\"confirmed\":true,\"destructiveFingerprint\":\"...\"}}"] },
      { description: "Instance method", argv: ["tools", "run", "reflection.method.call", "--input", "{\"typeName\":\"UnityEngine.Transform\",\"methodName\":\"Translate\",\"args\":[1,0,0],\"instanceRef\":{\"instanceId\":123},\"security\":{\"confirmed\":true,\"destructiveFingerprint\":\"...\"}}"] }
    ]
  },
  {
    name: "type.schema",
    title: "Type JSON Schema",
    description: "Generate a live JSON Schema for any C# / Unity type by reflecting over its public fields and properties. Useful before object.modify/component.modify to understand writable structure.",
    group: "type",
    inputSchema: objSchema({
      typeName: { type: "string", description: "Full type name, e.g. UnityEngine.Rigidbody." },
      maxDepth: { type: "integer", default: 3, description: "Max schema nesting depth." }
    }, ["typeName"]),
    outputSchema: objSchema({
      command: { type: "string" },
      typeName: { type: "string" },
      schema: { type: "object" }
    }),
    destructiveHint: false,
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
