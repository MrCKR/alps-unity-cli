import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const GO_TOOLS: ToolMetadata[] = [
  {
    name: "go.find",
    title: "Find GameObject",
    description: "Find a GameObject by name in the active scene hierarchy.",
    group: "go",
    inputSchema: objSchema({ name: { type: "string" } }, ["name"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run go.find",
    docsTopic: "go",
    examples: [
      { description: "Find by name", argv: ["tools", "run", "go.find", "--name", "Player"] }
    ]
  },
  {
    name: "go.inspect",
    title: "Inspect GameObject",
    description: "Get components, children and transform data of a GameObject.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.inspect",
    docsTopic: "go"
  },
  {
    name: "go.create",
    title: "Create GameObject",
    description: "Create a new empty GameObject in the active scene.",
    group: "go",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" } }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.create",
    docsTopic: "go"
  },
  {
    name: "go.modify",
    title: "Modify GameObject",
    description: "Modify transform or other properties of a GameObject.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.modify",
    docsTopic: "go"
  },
  {
    name: "go.duplicate",
    title: "Duplicate GameObject",
    description: "Duplicate a GameObject in the active scene, optionally assigning a new name or parent.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" }, name: { type: "string" }, parent: { type: "string" } }, ["target"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.duplicate",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.duplicate"
  },
  {
    name: "go.move-relative",
    title: "Move GameObject Relative",
    description: "Move a GameObject by a relative offset without replacing its absolute transform.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" }, offsetX: { type: "number" }, offsetY: { type: "number" }, offsetZ: { type: "number" } }, ["target"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.move-relative",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.move_relative"
  },
  {
    name: "go.look-at",
    title: "Rotate GameObject To Look At Target",
    description: "Rotate a GameObject so its forward vector looks at another GameObject.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" }, lookAt: { type: "string" } }, ["target", "lookAt"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.look-at",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.look_at"
  },
  {
    name: "go.delete",
    title: "Delete GameObject",
    description: "Delete a GameObject from the scene. This action is irreversible.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    destructiveHint: true,
    supportsDryRun: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.delete",
    docsTopic: "go"
  },
  {
    name: "go.set-parent",
    title: "Set GameObject Parent",
    description: "Reparent a GameObject under another GameObject or to the scene root.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" }, parent: { type: "string", description: "Parent path/name; omit to detach to root." }, worldPositionStays: { type: "boolean", default: true } }, ["target"]),
    destructiveHint: true,
    supportsDryRun: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.set-parent",
    docsTopic: "go",
    equivalentUnityMcp: "gameobject-set-parent"
  }
];

export const COMPONENT_TOOLS: ToolMetadata[] = [
  {
    name: "component.add",
    title: "Add Component",
    description: "Add a component to a GameObject.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" } }, ["target", "component"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.add",
    docsTopic: "component"
  },
  {
    name: "component.remove",
    title: "Remove Component",
    description: "Remove a component from a GameObject. Irreversible.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" } }, ["target", "component"]),
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.remove",
    docsTopic: "component"
  },
  {
    name: "component.set",
    title: "Set Component Property",
    description: "Set a property value on a component attached to a GameObject.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" }, property: { type: "string" }, value: { type: "string" } }, ["target", "component", "property", "value"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.set",
    docsTopic: "component"
  },
  {
    name: "component.get",
    title: "Get Component Data",
    description: "Deep-read a single Component into a SerializedMember tree with fields/props/value/paths. Supports instanceId/componentIndex/componentType resolution, maxDepth, paths, includeFields/includeProperties. Aligns with IvanMurzak gameobject-component-get.",
    group: "component",
    inputSchema: objSchema({
      instanceId: { type: "integer", description: "GameObject instance ID." },
      path: { type: "string", description: "GameObject hierarchy path or name." },
      componentInstanceId: { type: "integer", description: "Component instance ID (highest priority)." },
      componentIndex: { type: "integer", description: "Component index on the GameObject." },
      component: { type: "string", description: "Component type name." },
      maxDepth: { type: "integer", default: 4 },
      includeFields: { type: "boolean", default: true },
      includeProperties: { type: "boolean", default: true },
      deepSerialization: { type: "boolean", default: true },
      paths: { type: "array", items: { type: "string" } }
    }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.get",
    docsTopic: "component",
    equivalentUnityMcp: "gameobject-component-get",
    examples: [
      { description: "Read Transform deeply", argv: ["tools", "run", "component.get", "--input", "{\"path\":\"Main Camera\",\"component\":\"Transform\"}"] }
    ]
  },
  {
    name: "component.modify",
    title: "Modify Component",
    description: "Apply pathPatches, jsonPatch (Merge Patch) or componentDiff to a Component. Returns updated data, changed paths, logs and audit. Use component.get first. Aligns with IvanMurzak gameobject-component-modify.",
    group: "component",
    inputSchema: objSchema({
      instanceId: { type: "integer" },
      path: { type: "string" },
      componentInstanceId: { type: "integer" },
      componentIndex: { type: "integer" },
      component: { type: "string" },
      pathPatches: { type: "array", items: { type: "object" } },
      jsonPatch: { type: "object" },
      componentDiff: { type: "object", description: "Alias of jsonPatch (Merge Patch semantics)." },
      maxDepth: { type: "integer", default: 2 }
    }),
    destructiveHint: true,
    supportsDryRun: true,
    riskLevel: "high",
    sideEffects: ["component-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.modify",
    docsTopic: "component",
    equivalentUnityMcp: "gameobject-component-modify",
    examples: [
      { description: "Patch localPosition", argv: ["tools", "run", "component.modify", "--input", "{\"path\":\"Player\",\"component\":\"Transform\",\"pathPatches\":[{\"path\":\"localPosition\",\"value\":{\"x\":1,\"y\":2,\"z\":3}}]}"] }
    ]
  },
  {
    name: "component.list-types",
    title: "List Component Types",
    description: "Enumerate Component types available in the current project (all loaded assemblies). Use before component.add/get/modify to discover type names. Aligns with IvanMurzak gameobject-component-list-all.",
    group: "component",
    inputSchema: objSchema({ keyword: { type: "string", description: "Optional name/namespace filter." }, pageSize: { type: "integer", default: 100 }, cursor: { type: "integer", default: 0 } }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.list-types",
    docsTopic: "component",
    equivalentUnityMcp: "gameobject-component-list-all",
    examples: [
      { description: "List Rigidbody types", argv: ["tools", "run", "component.list-types", "--input", "{\"keyword\":\"Rigidbody\"}"] }
    ]
  }
];
