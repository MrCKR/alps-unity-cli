import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const UI_TOOLS: ToolMetadata[] = [
  {
    name: "ui.canvas.create",
    title: "Create UI Canvas",
    description: "Create a new UI Canvas in the scene.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string" } }, ["name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui canvas/create",
    docsTopic: "ui"
  },
  {
    name: "ui.text.create",
    title: "Create UI Text",
    description: "Create a UI Text element under a parent.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" }, text: { type: "string" }, width: { type: "integer" }, height: { type: "integer" } }, ["name", "parent"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui text/create",
    docsTopic: "ui"
  },
  {
    name: "ui.button.create",
    title: "Create UI Button",
    description: "Create a UI Button element under a parent.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" }, label: { type: "string" } }, ["name", "parent"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui button/create",
    docsTopic: "ui"
  },
  {
    name: "ui.image.create",
    title: "Create UI Image",
    description: "Create a UI Image element under a parent.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" }, sprite: { type: "string" } }, ["name", "parent"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui image/create",
    docsTopic: "ui"
  },
  {
    name: "ui.document.info",
    title: "UI Document Info",
    description: "Get info on a UI Document (UIToolkit) or read a UXML asset.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string" }, uss: { type: "string" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui document/info",
    docsTopic: "ui"
  },
  {
    name: "ui.document.create",
    title: "Create UI Document",
    description: "Create a new UI Document from a UXML template.",
    group: "ui",
    inputSchema: objSchema({ uxml: { type: "string" }, uss: { type: "string" }, panelSettings: { type: "string" }, overwrite: { type: "boolean" } }, ["uxml"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui document/create",
    docsTopic: "ui"
  },
  {
    name: "ui.hierarchy",
    title: "UI Hierarchy",
    description: "List UI elements in a canvas or UI Document hierarchy.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui hierarchy",
    docsTopic: "ui"
  }
];
