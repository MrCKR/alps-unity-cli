import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const UI_TOOLS: ToolMetadata[] = [
  {
    name: "ui.canvas.create",
    summary: "Create a new uGUI Canvas GameObject in the active scene (with Canvas + CanvasScaler + GraphicRaycaster).",
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
    summary: "Create a uGUI Text element under a parent Canvas/RectTransform. parent resolves by name, hierarchy path or instance id.",
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
    summary: "Create a uGUI Button element (with Text child) under a parent Canvas/RectTransform. parent resolves by name, hierarchy path or instance id.",
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
    summary: "Create a uGUI Image element under a parent Canvas/RectTransform.",
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
    summary: "Read info on a UI Toolkit UIDocument component (on a GameObject) or a UXML asset. target resolves to a UIDocument GameObject or an Assets/*.uxml path.",
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
    summary: "Create a new UI Toolkit UI Document (.uxml) asset from a UXML template.",
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
    name: "ui.document.update",
    summary: "Replace the content of an existing UXML asset and force Unity to reimport it.",
    description: "Replace the content of an existing UXML asset and force Unity to reimport it. This is a persistent asset file write; use --dry-run to preview byte size and target before --yes. Use ui.document.info after update to inspect import state.",
    group: "ui",
    inputSchema: objSchema({ uxml: { type: "string", description: "Existing Assets/*.uxml path. path is accepted as an alias." }, path: { type: "string", description: "Alias of uxml." }, content: { type: "string", description: "Replacement UXML text; --content-file is also supported." } }, ["uxml", "content"]),
    outputSchema: objSchema({ document: { type: "object" }, sizeBytes: { type: "integer" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "medium",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.document.update",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.update_ui_document",
    examples: [
      { description: "Preview replacing UXML content", argv: ["tools", "run", "ui.document.update", "--input", "{\"uxml\":\"Assets/UnityCliAlignmentFixtures/GeneratedUI.uxml\",\"content\":\"<ui:UXML xmlns:ui=\\\"UnityEngine.UIElements\\\" />\"}", "--dry-run"] }
    ]
  },
  {
    name: "ui.document.delete",
    summary: "Delete an existing UXML or USS UI Toolkit asset.",
    description: "Delete an existing UXML or USS UI Toolkit asset. This is persistent asset deletion; use --dry-run before --yes when the path was discovered programmatically.",
    group: "ui",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.uxml or Assets/*.uss path." }, uxml: { type: "string", description: "Alias for a UXML path." } }, ["path"]),
    outputSchema: objSchema({ document: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["asset-delete", "asset-import"],
    riskLevel: "high",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.document.delete",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.delete_ui_document",
    examples: [
      { description: "Preview deleting a generated UXML", argv: ["tools", "run", "ui.document.delete", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedUI.uxml\"}", "--dry-run"] }
    ]
  },
  {
    name: "ui.hierarchy",
    summary: "List UI elements under a Canvas (uGUI) or UI Document (UIToolkit) hierarchy.",
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
    summary: "Attach a UIDocument component to a GameObject, optionally with a UXML source asset and PanelSettings.",
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
    name: "ui.detach-document",
    summary: "Remove a UIDocument component from a GameObject without deleting UXML or PanelSettings assets.",
    description: "Remove a UIDocument component from a GameObject without deleting UXML or PanelSettings assets. This only changes the current scene/session until the scene is saved; use --dry-run to preview the target component removal.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string", description: "Target GameObject with UIDocument: name, hierarchy path or instance id." } }, ["target"]),
    outputSchema: objSchema({ target: { type: "object" }, removed: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "medium",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.detach-document",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.detach_ui_document",
    examples: [
      { description: "Preview detaching a UIDocument", argv: ["tools", "run", "ui.detach-document", "--input", "{\"target\":\"UIDoc\"}", "--dry-run"] }
    ]
  },
  {
    name: "ui.create-panel-settings",
    summary: "Create a PanelSettings asset with scale mode and reference resolution.",
    description: "Create a PanelSettings asset with scale mode and reference resolution. Destructive: writes a new asset (overwrites existing at path). Aligns with CoplayDev manage_ui.create_panel_settings.",
    group: "ui",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/*.asset PanelSettings path." }, scaleMode: { type: "string", enum: ["ConstantPixelSize", "ConstantPhysicalSize", "ScaleWithScreenSize"], default: "ScaleWithScreenSize", description: "Panel scale mode." }, referenceWidth: { type: "integer", default: 1920, description: "Reference resolution width." }, referenceHeight: { type: "integer", default: 1080, description: "Reference resolution height." }, sortingOrder: { type: "integer", description: "Optional PanelSettings sortingOrder/sortOrder when available in this Unity version." }, sortingLayerName: { type: "string", description: "Optional sorting layer name when available." }, targetTexture: { type: "string", description: "Optional RenderTexture asset path for PanelSettings.targetTexture when available." }, screenMatchMode: { type: "string", description: "Optional PanelScreenMatchMode enum when available." }, match: { type: "number", description: "Optional screen match value when available." }, clearColor: { type: "string", description: "Optional clear color when available." } }, ["path"]),
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
    name: "ui.update-panel-settings",
    summary: "Update an existing PanelSettings asset's scale mode and reference resolution.",
    description: "Update an existing PanelSettings asset's scale mode and reference resolution. This is a persistent asset change; use --dry-run before --yes when adjusting project UI settings.",
    group: "ui",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.asset PanelSettings path." }, scaleMode: { type: "string", enum: ["ConstantPixelSize", "ConstantPhysicalSize", "ScaleWithScreenSize"], description: "Panel scale mode." }, referenceWidth: { type: "integer", description: "Reference resolution width." }, referenceHeight: { type: "integer", description: "Reference resolution height." }, sortingOrder: { type: "integer", description: "Optional PanelSettings sortingOrder/sortOrder when available in this Unity version." }, sortingLayerName: { type: "string", description: "Optional sorting layer name when available." }, targetTexture: { type: "string", description: "Optional RenderTexture asset path for PanelSettings.targetTexture when available." }, screenMatchMode: { type: "string", description: "Optional PanelScreenMatchMode enum when available." }, match: { type: "number", description: "Optional screen match value when available." }, clearColor: { type: "string", description: "Optional clear color when available." } }, ["path"]),
    outputSchema: objSchema({ path: { type: "string" }, scaleMode: { type: "string" }, referenceResolution: { type: "string" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "medium",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.update-panel-settings",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.update_panel_settings",
    examples: [
      { description: "Preview changing PanelSettings resolution", argv: ["tools", "run", "ui.update-panel-settings", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedPanel.asset\",\"referenceWidth\":1280,\"referenceHeight\":720}", "--dry-run"] }
    ]
  },
  {
    name: "ui.get-visual-tree",
    summary: "Inspect the visual tree of a UIDocument: element name, type, classes, child count up to maxDepth.",
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
  },
  {
    name: "ui.modify-visual-element",
    summary: "Modify a live UIDocument VisualElement's classes, text or inline style without writing UXML.",
    description: "Modify a live UIDocument VisualElement's classes, text or inline style without writing UXML. Use ui.get-visual-tree first to discover elementName. Changes affect the current Editor/PlayMode panel session and are not persisted to the UXML asset.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string", description: "UIDocument GameObject name, hierarchy path or instance id." }, elementName: { type: "string", description: "VisualElement name discovered from ui.get-visual-tree." }, addClass: { type: "string", description: "Class to add." }, removeClass: { type: "string", description: "Class to remove." }, text: { type: "string", description: "Text for Label/TextElement targets." }, display: { type: "string", enum: ["flex", "none"], description: "Set display style." }, width: { type: "number", description: "Inline width style." }, height: { type: "number", description: "Inline height style." }, backgroundColor: { type: "string", description: "Inline background color." } }, ["target", "elementName"]),
    outputSchema: objSchema({ target: { type: "object" }, element: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    sideEffects: ["ui-session-modify"],
    riskLevel: "low",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.modify-visual-element",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.modify_visual_element",
    examples: [
      { description: "Preview adding a class to a VisualElement", argv: ["tools", "run", "ui.modify-visual-element", "--input", "{\"target\":\"UIDoc\",\"elementName\":\"Root\",\"addClass\":\"selected\"}", "--dry-run"] }
    ]
  },
  {
    name: "ui.link-stylesheet",
    summary: "Link an existing USS stylesheet into an existing UXML file using Unity's project://database path format.",
    description: "Link an existing USS stylesheet into an existing UXML file using Unity's project://database path format. This edits the UXML asset; use --dry-run to see whether a change is needed before --yes.",
    group: "ui",
    inputSchema: objSchema({ uxml: { type: "string", description: "Existing Assets/*.uxml path." }, uss: { type: "string", description: "Existing Assets/*.uss path." }, styleSheet: { type: "string", description: "Alias of uss." } }, ["uxml", "uss"]),
    outputSchema: objSchema({ document: { type: "object" }, styleSheet: { type: "object" }, changed: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "medium",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.link-stylesheet",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.link_stylesheet",
    examples: [
      { description: "Preview linking a stylesheet", argv: ["tools", "run", "ui.link-stylesheet", "--input", "{\"uxml\":\"Assets/UI/Layout.uxml\",\"uss\":\"Assets/UI/Styles.uss\"}", "--dry-run"] }
    ]
  },
  {
    name: "ui.list-documents",
    summary: "List UI Toolkit UXML, USS and PanelSettings assets for discovery.",
    description: "List UI Toolkit UXML, USS and PanelSettings assets for discovery. Use kind=uxml, uss, panelSettings or all; pageSize is clamped by the bridge.",
    group: "ui",
    inputSchema: objSchema({ kind: { type: "string", enum: ["all", "uxml", "uss", "panelSettings"], default: "all", description: "Asset kind filter." }, pageSize: { type: "integer", default: 100, description: "Maximum items returned." } }),
    outputSchema: objSchema({ count: { type: "integer" }, items: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.list-documents",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.list_documents",
    examples: [
      { description: "List UXML documents", argv: ["tools", "run", "ui.list-documents", "--input", "{\"kind\":\"uxml\",\"pageSize\":50}"] }
    ]
  },
  {
    name: "ui.render-preview",
    summary: "Render or export a Unity AssetPreview PNG for a UXML asset or UIDocument target.",
    description: "Render or export a Unity AssetPreview PNG for a UXML asset or UIDocument target. This writes a PNG artifact under Assets/ so it is destructive and requires --yes unless --dry-run is used. If Unity has not generated an AssetPreview yet, the tool returns E_UI_PREVIEW_UNAVAILABLE with retry guidance.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string", description: "Assets/*.uxml path or UIDocument GameObject." }, uxml: { type: "string", description: "Alias for a UXML asset target." }, output: { type: "string", default: "Assets/UnityCliArtifacts/UI/ui-preview.png", description: "Destination Assets/*.png artifact path." } }, ["target"]),
    outputSchema: objSchema({ target: { type: "string" }, sourceAsset: { type: "string" }, output: { type: "string" }, width: { type: "integer" }, height: { type: "integer" }, sizeBytes: { type: "integer" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["asset-write", "asset-import", "artifact-write"],
    riskLevel: "low",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run ui.render-preview",
    docsTopic: "ui",
    equivalentUnityMcp: "manage_ui.render_preview",
    examples: [
      { description: "Preview rendering a UXML artifact", argv: ["tools", "run", "ui.render-preview", "--input", "{\"target\":\"Assets/UI/Layout.uxml\",\"output\":\"Assets/UnityCliArtifacts/UI/layout.png\"}", "--dry-run"] }
    ]
  }
];
