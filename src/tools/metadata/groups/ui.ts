import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const UI_TOOLS: ToolMetadata[] = [
  {
    name: "ui.canvas.create",
    title: "Create UI Canvas",
    description: "Create a new uGUI Canvas GameObject in the active scene (with Canvas + CanvasScaler + GraphicRaycaster). Use this as the parent root before creating Text/Button/Image elements.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string", description: "Canvas GameObject name." } }, ["name"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.canvas.create",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.create_canvas",
    examples: [
      { description: "Create a UI Canvas", argv: ["tools", "run", "ui.canvas.create", "--input", "{\"name\":\"MainCanvas\"}"] }
    ]
  },
  {
    name: "ui.text.create",
    title: "Create UI Text",
    description: "Create a uGUI Text element under a parent Canvas/RectTransform. parent resolves by name, hierarchy path or instance id. Create a Canvas first via ui.canvas.create if none exists.",
    group: "ui",
    inputSchema: objSchema({
      name: { type: "string", description: "Text GameObject name." },
      parent: { type: "string", description: "Parent Canvas or RectTransform: name, hierarchy path or instance id." },
      text: { type: "string", description: "Initial text content." },
      width: { type: "integer", description: "RectTransform width." },
      height: { type: "integer", description: "RectTransform height." },
      fontSize: { type: "integer", description: "Font size." }
    }, ["name", "parent"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.text.create",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.create_text",
    examples: [
      { description: "Create a UI Text under a canvas", argv: ["tools", "run", "ui.text.create", "--input", "{\"name\":\"Title\",\"parent\":\"MainCanvas\",\"text\":\"Hello\",\"fontSize\":24}"] }
    ]
  },
  {
    name: "ui.button.create",
    title: "Create UI Button",
    description: "Create a uGUI Button element (with Text child) under a parent Canvas/RectTransform. parent resolves by name, hierarchy path or instance id.",
    group: "ui",
    inputSchema: objSchema({
      name: { type: "string", description: "Button GameObject name." },
      parent: { type: "string", description: "Parent Canvas or RectTransform: name, hierarchy path or instance id." },
      label: { type: "string", description: "Button label text." }
    }, ["name", "parent"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.button.create",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.create_button",
    examples: [
      { description: "Create a UI Button under a canvas", argv: ["tools", "run", "ui.button.create", "--input", "{\"name\":\"StartBtn\",\"parent\":\"MainCanvas\",\"label\":\"Start\"}"] }
    ]
  },
  {
    name: "ui.image.create",
    title: "Create UI Image",
    description: "Create a uGUI Image element under a parent Canvas/RectTransform. Optionally assign a Sprite asset. parent resolves by name, hierarchy path or instance id.",
    group: "ui",
    inputSchema: objSchema({
      name: { type: "string", description: "Image GameObject name." },
      parent: { type: "string", description: "Parent Canvas or RectTransform: name, hierarchy path or instance id." },
      sprite: { type: "string", description: "Optional Assets/* sprite path." }
    }, ["name", "parent"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.image.create",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.create_image",
    examples: [
      { description: "Create a UI Image under a canvas", argv: ["tools", "run", "ui.image.create", "--input", "{\"name\":\"Icon\",\"parent\":\"MainCanvas\",\"sprite\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeTexture.png\"}"] }
    ]
  },
  {
    name: "ui.document.info",
    title: "UI Document Info",
    description: "Read info on a UI Toolkit UIDocument component (on a GameObject) or a UXML asset. target resolves to a UIDocument GameObject or an Assets/*.uxml path. Read-only.",
    group: "ui",
    inputSchema: objSchema({
      target: { type: "string", description: "UIDocument GameObject name/path/instance id, or an Assets/*.uxml path." },
      uss: { type: "string", description: "Optional Assets/*.uss stylesheet to include in the report." }
    }, ["target"]),
    outputSchema: objSchema({ command: { type: "string" }, target: { type: "string" }, hasDocument: { type: "boolean" }, visualTree: { type: "object" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.document.info",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.get_ui_document_info",
    examples: [
      { description: "Inspect a UIDocument GameObject", argv: ["tools", "run", "ui.document.info", "--input", "{\"target\":\"UIDoc\"}"] }
    ]
  },
  {
    name: "ui.document.create",
    title: "Create UI Document",
    description: "Create a new UI Toolkit UI Document (.uxml) asset from a UXML template. Optionally pair a .uss stylesheet and a PanelSettings. Refuses to overwrite unless overwrite is true.",
    group: "ui",
    inputSchema: objSchema({
      uxml: { type: "string", description: "Destination Assets/*.uxml path." },
      uss: { type: "string", description: "Optional Assets/*.uss stylesheet path to create/reference." },
      panelSettings: { type: "string", description: "Optional PanelSettings asset to associate." },
      overwrite: { type: "boolean", description: "Allow replacing an existing .uxml." }
    }, ["uxml"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.document.create",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.create_ui_document",
    examples: [
      { description: "Create a UI Document", argv: ["tools", "run", "ui.document.create", "--input", "{\"uxml\":\"Assets/UnityCliAlignmentFixtures/GeneratedUI.uxml\"}"] }
    ]
  },
  {
    name: "ui.hierarchy",
    title: "UI Hierarchy",
    description: "List UI elements under a Canvas (uGUI) or UI Document (UIToolkit) hierarchy. Paged by pageSize/cursor. Use this to inspect UI structure before modifying elements.",
    group: "ui",
    inputSchema: objSchema({
      target: { type: "string", description: "Canvas or UIDocument GameObject: name, hierarchy path or instance id." },
      pageSize: { type: "integer", default: 100, description: "Maximum elements per page." },
      cursor: { type: "string", description: "Cursor from a previous pageInfo.nextCursor." }
    }),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, elements: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.hierarchy",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.get_ui_hierarchy",
    examples: [
      { description: "List UI elements under a canvas", argv: ["tools", "run", "ui.hierarchy", "--input", "{\"target\":\"MainCanvas\",\"pageSize\":50}"] }
    ]
  },
  {
    name: "ui.attach-document",
    title: "Attach UIDocument",
    description: "Attach a UIDocument component to a GameObject, optionally with a UXML source asset and PanelSettings. Destructive: modifies the target GameObject. Aligns with CoplayDev manage_ui.attach_ui_document.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string", description: "Target GameObject: name, hierarchy path or instance id." }, sourceAsset: { type: "string", description: "Assets/*.uxml source asset." }, panelSettings: { type: "string", description: "PanelSettings asset path." }, sortOrder: { type: "integer", default: 0, description: "Sorting order." } }, ["target"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.attach-document",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.attach_ui_document",
    examples: [
      { description: "Attach a UIDocument to a GameObject", argv: ["tools", "run", "ui.attach-document", "--input", "{\"target\":\"UIDoc\",\"sourceAsset\":\"Assets/UnityCliAlignmentFixtures/GeneratedUI.uxml\"}"] }
    ]
  },
  {
    name: "ui.create-panel-settings",
    title: "Create PanelSettings",
    description: "Create a PanelSettings asset with scale mode and reference resolution. Destructive: writes a new asset (overwrites existing at path). Aligns with CoplayDev manage_ui.create_panel_settings.",
    group: "ui",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/*.asset PanelSettings path." }, scaleMode: { type: "string", enum: ["ConstantPixelSize", "ConstantPhysicalSize", "ScaleWithScreenSize"], default: "ScaleWithScreenSize", description: "Panel scale mode." }, referenceWidth: { type: "integer", default: 1920, description: "Reference resolution width." }, referenceHeight: { type: "integer", default: 1080, description: "Reference resolution height." } }, ["path"]),
    destructiveHint: true,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.create-panel-settings",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.create_panel_settings",
    examples: [
      { description: "Create a PanelSettings asset", argv: ["tools", "run", "ui.create-panel-settings", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedPanel.asset\",\"referenceWidth\":1920,\"referenceHeight\":1080}"] }
    ]
  },
  {
    name: "ui.get-visual-tree",
    title: "Get Visual Tree",
    description: "Inspect the visual tree of a UIDocument: element name, type, classes, child count up to maxDepth. Read-only. Aligns with CoplayDev manage_ui.get_visual_tree.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string", description: "UIDocument GameObject: name, hierarchy path or instance id." }, maxDepth: { type: "integer", default: 10, description: "Maximum traversal depth." } }, ["target"]),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, elements: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.get-visual-tree",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.get_visual_tree",
    examples: [
      { description: "Inspect a UIDocument visual tree", argv: ["tools", "run", "ui.get-visual-tree", "--input", "{\"target\":\"UIDoc\",\"maxDepth\":10}"] }
    ]
  }
];
