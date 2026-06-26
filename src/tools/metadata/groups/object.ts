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
    description: "Deep-serialize any UnityEngine.Object (GameObject, Component, Asset, ScriptableObject) into a SerializedMember tree. Use asset.get-data for known asset paths and component.get for a single component. For token safety, pass exact paths or one viewQuery; they are mutually exclusive. Keep maxDepth low for full reads.",
    group: "object",
    inputSchema: objSchema({
      instanceId: { type: "integer", description: "Unity Object instance ID (highest priority)." },
      assetPath: { type: "string", description: "Asset path under Assets/." },
      guid: { type: "string", description: "Asset GUID." },
      path: { type: "string", description: "GameObject hierarchy path or name." },
      name: { type: "string", description: "GameObject name (alias of path)." },
      maxDepth: { type: "integer", default: 4, description: "Max serialization depth for full reads or viewQuery subtree." },
      includeFields: { type: "boolean", default: true },
      includeProperties: { type: "boolean", default: true },
      deepSerialization: { type: "boolean", default: true },
      paths: { type: "array", items: { type: "string" }, description: "Exact member paths to read as a small aggregate, e.g. [\"name\"] for assets or [\"transform.position\"] for scene objects. Mutually exclusive with viewQuery." },
      viewQuery: { type: "object", description: "Optional subtree/filter read: {path?, namePattern?, typeFilter?, maxDepth?}. Mutually exclusive with paths." },
      namePattern: { type: "string", description: "Comma-separated exact member-name filter for full reads; prefer paths/viewQuery for token safety." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      typeName: { type: "string", description: "Serialized type or UnityCli.PathReadAggregate for paths reads." },
      instanceId: { type: "integer" },
      path: { type: "string", description: "SerializedMember root path." },
      fields: { type: "array", items: { type: "object" }, description: "Present for path aggregate or expanded field data." },
      props: { type: "array", items: { type: "object" }, description: "Present for property data when included." },
      unityObjectRef: { type: "object" }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run object.get-data",
    docsTopic: "object",
    equivalentUnityMcp: "object-get-data",
    examples: [
      { description: "Read by instanceId", argv: ["tools", "run", "object.get-data", "--input", "{\"instanceId\":12345}"] },
      { description: "Read an asset name only", argv: ["tools", "run", "object.get-data", "--input", "{\"assetPath\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\",\"paths\":[\"name\"],\"maxDepth\":2}"] },
      { description: "Read a filtered asset subtree", argv: ["tools", "run", "object.get-data", "--input", "{\"assetPath\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\",\"viewQuery\":{\"namePattern\":\"name\",\"maxDepth\":1}}"] }
    ]
  },
  {
    name: "object.modify",
    title: "Modify Object",
    description: "Apply pathPatches or jsonPatch (JSON Merge Patch, RFC 7386) to any Unity Object. Returns updated data, changed paths, logs and structured audit. Use object.get-data first to read current values. In Play Mode, scene/runtime object changes are runtime-only and asset changes can affect the editor session.",
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
    playModeSupport: "runtime-only",
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
