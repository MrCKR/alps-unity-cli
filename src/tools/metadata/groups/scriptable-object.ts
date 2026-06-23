import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const SCRIPTABLE_OBJECT_TOOLS: ToolMetadata[] = [
  {
    name: "scriptable-object.create",
    title: "Create ScriptableObject",
    description: "Create a ScriptableObject asset of a specified type under Assets/.",
    group: "scriptable-object",
    inputSchema: objSchema({ path: { type: "string" }, type: { type: "string" }, overwrite: { type: "boolean" } }, ["path", "type"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.create",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.create"
  },
  {
    name: "scriptable-object.info",
    title: "ScriptableObject Info",
    description: "Read ScriptableObject asset type, guid, and public readable properties.",
    group: "scriptable-object",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.info",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.info"
  },
  {
    name: "scriptable-object.modify",
    title: "Modify ScriptableObject",
    description: "Set a public writable field or property on a ScriptableObject asset.",
    group: "scriptable-object",
    inputSchema: objSchema({ path: { type: "string" }, property: { type: "string" }, value: { type: "string" } }, ["path", "property", "value"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scriptable-object.modify",
    docsTopic: "scriptable-object",
    equivalentUnityMcp: "manage_scriptable_object.modify"
  }
];
