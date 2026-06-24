import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Object 域：通用 Unity Object 深读写。对齐 IvanMurzak object-get-data/object-modify。
 * 底层复用统一 ObjectRef/SerializedMember/serializer/patch 基础设施。
 */
export const OBJECT_TOOLS: ToolMetadata[] = [
  {
    name: "object.get-data",
    title: "Get Object Data",
    description: "Deep-serialize any UnityEngine.Object (GameObject, Component, Asset, ScriptableObject) into a SerializedMember tree with fields/props/value/paths. Supports maxDepth, paths, includeFields/includeProperties, namePattern for partial reads.",
    group: "object",
    inputSchema: objSchema({
      instanceId: { type: "integer", description: "Unity Object instance ID (highest priority)." },
      assetPath: { type: "string", description: "Asset path under Assets/." },
      guid: { type: "string", description: "Asset GUID." },
      path: { type: "string", description: "GameObject hierarchy path or name." },
      name: { type: "string", description: "GameObject name (alias of path)." },
      maxDepth: { type: "integer", default: 4, description: "Max serialization depth." },
      includeFields: { type: "boolean", default: true },
      includeProperties: { type: "boolean", default: true },
      deepSerialization: { type: "boolean", default: true },
      paths: { type: "array", items: { type: "string" }, description: "Only read these dotted paths (e.g. [\"localPosition\",\"m_Color\"])." },
      namePattern: { type: "string", description: "Comma-separated member name filter." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      typeName: { type: "string" },
      instanceId: { type: "integer" },
      data: { type: "object" }
    }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run object.get-data",
    docsTopic: "object",
    equivalentUnityMcp: "object-get-data",
    examples: [
      { description: "Read by instanceId", argv: ["tools", "run", "object.get-data", "--input", "{\"instanceId\":12345}"] },
      { description: "Read Transform localPosition only", argv: ["tools", "run", "object.get-data", "--input", "{\"path\":\"Main Camera\",\"paths\":[\"localPosition\"]}"] }
    ]
  },
  {
    name: "object.modify",
    title: "Modify Object",
    description: "Apply pathPatches or jsonPatch (JSON Merge Patch, RFC 7386) to any Unity Object. Returns updated data, changed paths, logs and structured audit. Use object.get-data first to read current values.",
    group: "object",
    inputSchema: objSchema({
      instanceId: { type: "integer" },
      assetPath: { type: "string" },
      guid: { type: "string" },
      path: { type: "string" },
      pathPatches: { type: "array", items: { type: "object" }, description: "Array of {path, value, op?}. op defaults to replace." },
      jsonPatch: { type: "object", description: "JSON Merge Patch object (RFC 7386)." },
      maxDepth: { type: "integer", default: 2 }
    }),
    destructiveHint: true,
    supportsDryRun: true,
    riskLevel: "high",
    sideEffects: ["object-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run object.modify",
    docsTopic: "object",
    equivalentUnityMcp: "object-modify",
    examples: [
      { description: "Patch by pathPatches", argv: ["tools", "run", "object.modify", "--input", "{\"instanceId\":123,\"pathPatches\":[{\"path\":\"m_Color\",\"value\":{\"r\":1,\"g\":0,\"b\":0,\"a\":1}}]}"] },
      { description: "Dry-run preview", argv: ["tools", "run", "object.modify", "--input", "{\"instanceId\":123,\"jsonPatch\":{\"m_Color\":{\"r\":1}}}", "--dry-run"] }
    ]
  }
];
