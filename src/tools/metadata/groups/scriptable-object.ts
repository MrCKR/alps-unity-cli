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
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "high",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.create",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.create",
    examples: [
      { description: "Preview ScriptableObject asset creation", argv: ["tools", "run", "scriptable-object.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfig.asset\",\"type\":\"UnityCliAlignmentFixtures.AlignmentConfig\"}", "--dry-run"] },
      { description: "Create a ScriptableObject asset after confirmation", argv: ["tools", "run", "scriptable-object.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfig.asset\",\"type\":\"UnityCliAlignmentFixtures.AlignmentConfig\"}", "--yes"] }
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
    name: "scriptable-object.list-types",
    summary: "List ScriptableObject types currently loaded in the Unity Editor domain.",
    description: "List concrete ScriptableObject types available to create or inspect. Filter by namePattern or assemblyPattern and use pageSize/cursor to keep output small. This is read-only and does not create or import assets.",
    group: "scriptable-object",
    inputSchema: objSchema({
      namePattern: { type: "string", description: "Optional case-insensitive substring matched against class name or full name." },
      assemblyPattern: { type: "string", description: "Optional case-insensitive substring matched against assembly name." },
      includeAbstract: { type: "boolean", description: "Include abstract ScriptableObject base classes. Defaults to false." },
      pageSize: { type: "integer", description: "Maximum items to return, clamped to 1..200. Defaults to 50." },
      cursor: { type: "string", description: "Zero-based cursor returned from pageInfo.nextCursor." }
    }),
    outputSchema: objSchema({
      count: { type: "integer" },
      items: { type: "array", items: objSchema({ name: { type: "string" }, fullName: { type: "string" }, assembly: { type: "string" }, namespace: { type: "string" }, abstract: { type: "boolean" } }) },
      pageInfo: { type: "object" }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.list-types",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.list_types",
    examples: [
      { description: "Find config ScriptableObject types", argv: ["tools", "run", "scriptable-object.list-types", "--input", "{\"namePattern\":\"Config\",\"pageSize\":20}"] }
    ]
  },
  {
    name: "scriptable-object.modify",
    summary: "Modify ScriptableObject asset fields through public members or SerializedObject patches.",
    description: "Modify a ScriptableObject asset. The legacy property/value form sets a public writable field or property. The patches[] form uses Unity SerializedObject propertyPath entries for serialized private fields, arrays and Unity value types; use dry-run to validate patches before writing. Supported patch values include primitive JSON values, enum names/indexes, colors, vectors, quaternions, null, and object references by asset path or guid. This edits the shared asset and is recorded in Undo.",
    group: "scriptable-object",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.asset path." },
      property: { type: "string", description: "Legacy public writable field or property name. Required only when patches is omitted." },
      value: { description: "Legacy member value or patch value. Use JSON booleans/numbers to preserve type where possible." },
      patches: {
        type: "array",
        description: "SerializedObject patch list. Each patch accepts propertyPath (or path), op=set|array_resize, and value.",
        items: objSchema({
          propertyPath: { type: "string", description: "Unity SerializedProperty path, e.g. speed or items[0].name." },
          op: { type: "string", description: "set or array_resize. Defaults to set." },
          value: { description: "JSON value to assign, or non-negative integer for array_resize." }
        }, ["propertyPath"])
      }
    }, ["path"]),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write"],
    riskLevel: "high",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.modify",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.modify",
    examples: [
      { description: "Validate SerializedObject patches", argv: ["tools", "run", "scriptable-object.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentConfigProbe.asset\",\"patches\":[{\"propertyPath\":\"MaxValue\",\"value\":100}]}", "--dry-run"] },
      { description: "Set an integer field after confirmation", argv: ["tools", "run", "scriptable-object.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentConfigProbe.asset\",\"property\":\"MaxValue\",\"value\":\"100\"}", "--yes"] }
    ]
  }
];
