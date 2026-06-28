import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const SCRIPTABLE_OBJECT_TOOLS: ToolMetadata[] = [
  {
    name: "scriptable-object.create",
    summary: "Create a ScriptableObject asset of a specified type at an Assets/*.asset path.",
    description: "Create a ScriptableObject asset of a specified type at an Assets/*.asset path. The type must derive from UnityEngine.ScriptableObject and be resolvable by full name or unique class name. Refuses to overwrite an existing asset unless overwrite is true. Use reflect.search or asset.search-built-in first to confirm the type name exists.",
    group: "scriptable-object",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/*.asset path. Intermediate folders are created." },
      type: { type: "string", description: "ScriptableObject type: full name (e.g. MyNamespace.MyConfig) or a unique class name." },
      overwrite: { type: "boolean", description: "Allow replacing an existing .asset at path." }
    }, ["path", "type"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.create",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.create",
    examples: [
      { description: "Create a ScriptableObject asset", argv: ["tools", "run", "scriptable-object.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfig.asset\",\"type\":\"UnityCliAlignmentFixtures.AlignmentConfig\"}"] }
    ]
  },
  {
    name: "scriptable-object.info",
    summary: "Read a ScriptableObject asset's type, guid, name and public readable field/property values.",
    description: "Read a ScriptableObject asset's type, guid, name and public readable field/property values. Use this before scriptable-object.modify to discover valid member names and types. Use asset.search with filter t:ScriptableObject to discover paths when unknown.",
    group: "scriptable-object",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.asset path." } }, ["path"]),
    outputSchema: objSchema({
      scriptableObject: objSchema({
        path: { type: "string" },
        guid: { type: "string" },
        name: { type: "string" },
        type: { type: "string", description: "Full ScriptableObject type name." },
        properties: { type: "object", description: "Public field/property name to string value." }
      })
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.info",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.info",
    examples: [
      { description: "Inspect a ScriptableObject asset", argv: ["tools", "run", "scriptable-object.info", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentConfigProbe.asset\"}"] }
    ]
  },
  {
    name: "scriptable-object.modify",
    summary: "Set a single public writable field or property on a ScriptableObject asset.",
    description: "Set a single public writable field or property on a ScriptableObject asset. Supported value types: string, bool, int, long, float, double, enum. Inspect valid member names with scriptable-object.info first; an unknown member returns E_SCRIPTABLE_OBJECT_PROPERTY_NOT_FOUND and an unsupported type returns E_SCRIPTABLE_OBJECT_VALUE_TYPE_UNSUPPORTED. This edits the shared asset and is recorded in Undo.",
    group: "scriptable-object",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.asset path." },
      property: { type: "string", description: "Public writable field or property name." },
      value: { type: "string", description: "Value as a string (parsed by the member type: bool, number, enum name, or string)." }
    }, ["path", "property", "value"]),
    destructiveHint: false,
    sideEffects: ["asset-write"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.modify",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.modify",
    examples: [
      { description: "Set an integer field on a ScriptableObject", argv: ["tools", "run", "scriptable-object.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentConfigProbe.asset\",\"property\":\"MaxValue\",\"value\":\"100\"}"] }
    ]
  }
];
