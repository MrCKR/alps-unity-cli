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
    cliCommand: "tools run ui.canvas.create",
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
    cliCommand: "tools run ui.text.create",
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
    cliCommand: "tools run ui.button.create",
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
    cliCommand: "tools run ui.image.create",
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
    cliCommand: "tools run ui.document.info",
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
    cliCommand: "tools run ui.document.create",
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
    cliCommand: "tools run ui.hierarchy",
    docsTopic: "ui"
  },
  {
    name: "ui.attach-document",
    title: "Attach UIDocument",
    description: "Attach a UIDocument component to a GameObject, optionally with a UXML source asset and PanelSettings. Aligns with CoplayDev manage_ui.attach_ui_document.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string" }, sourceAsset: { type: "string" }, panelSettings: { type: "string" }, sortOrder: { type: "integer", default: 0 } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.attach-document",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.attach_ui_document"
  },
  {
    name: "ui.create-panel-settings",
    title: "Create PanelSettings",
    description: "Create a PanelSettings asset with scale mode and reference resolution. Aligns with CoplayDev manage_ui.create_panel_settings.",
    group: "ui",
    inputSchema: objSchema({ path: { type: "string" }, scaleMode: { type: "string", enum: ["ConstantPixelSize", "ConstantPhysicalSize", "ScaleWithScreenSize"], default: "ScaleWithScreenSize" }, referenceWidth: { type: "integer", default: 1920 }, referenceHeight: { type: "integer", default: 1080 } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.create-panel-settings",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.create_panel_settings"
  },
  {
    name: "ui.get-visual-tree",
    title: "Get Visual Tree",
    description: "Inspect the visual tree of a UIDocument: element name, type, classes, child count up to maxDepth. Aligns with CoplayDev manage_ui.get_visual_tree.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string" }, maxDepth: { type: "integer", default: 10 } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.get-visual-tree",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.get_visual_tree"
  }
];
