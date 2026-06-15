import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const GO_TOOLS: ToolMetadata[] = [
  {
    name: "go.find",
    title: "Find GameObject",
    description: "Find a GameObject by name in the active scene hierarchy.",
    group: "go",
    inputSchema: objSchema({ name: { type: "string" } }, ["name"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "go find",
    docsTopic: "go",
    examples: [
      { description: "Find by name", argv: ["go", "find", "--name", "Player"] }
    ]
  },
  {
    name: "go.inspect",
    title: "Inspect GameObject",
    description: "Get components, children and transform data of a GameObject.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "go inspect",
    docsTopic: "go"
  },
  {
    name: "go.create",
    title: "Create GameObject",
    description: "Create a new empty GameObject in the active scene.",
    group: "go",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "go create",
    docsTopic: "go"
  },
  {
    name: "go.modify",
    title: "Modify GameObject",
    description: "Modify transform or other properties of a GameObject.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "go modify",
    docsTopic: "go"
  },
  {
    name: "go.delete",
    title: "Delete GameObject",
    description: "Delete a GameObject from the scene. This action is irreversible.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "go delete",
    docsTopic: "go"
  }
];

export const COMPONENT_TOOLS: ToolMetadata[] = [
  {
    name: "component.add",
    title: "Add Component",
    description: "Add a component to a GameObject.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" } }, ["target", "component"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "component add",
    docsTopic: "component"
  },
  {
    name: "component.remove",
    title: "Remove Component",
    description: "Remove a component from a GameObject. Irreversible.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" } }, ["target", "component"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "component remove",
    docsTopic: "component"
  },
  {
    name: "component.set",
    title: "Set Component Property",
    description: "Set a property value on a component attached to a GameObject.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" }, property: { type: "string" }, value: { type: "string" } }, ["target", "component", "property", "value"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "component set",
    docsTopic: "component"
  }
];
