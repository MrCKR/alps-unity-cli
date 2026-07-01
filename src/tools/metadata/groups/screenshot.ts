import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

// 截图通用输出 schema：artifact 路径、尺寸、格式与可选 diagnostics
const SCREENSHOT_OUTPUT = objSchema({
  command: { type: "string" },
  path: { type: "string", description: "Saved artifact path under Assets/." },
  width: { type: "integer" },
  height: { type: "integer" },
  sizeBytes: { type: "integer" },
  format: { type: "string" },
  camera: { type: "string" },
  diagnostics: { type: "array", items: { type: "object" }, description: "Present when includeDiagnostics is requested." },
  imageBase64: { type: "string", description: "Present only when inlineBase64=true." },
  imageWidth: { type: "integer" },
  imageHeight: { type: "integer" },
  imageMimeType: { type: "string" }
});

export const SCREENSHOT_TOOLS: ToolMetadata[] = [
  {
    name: "screenshot.capture",
    summary: "Compatibility alias: capture active SceneView or main Camera; prefer scene-view/game-view for explicit source.",
    description: "Compatibility alias that captures the active SceneView (or main Camera when no SceneView is open) to an Assets/* image path. Prefer screenshot.scene-view or screenshot.game-view when the source matters. Returns the saved artifact path and size.",
    group: "screenshot",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/* image path (.png/.jpg)." },
      width: { type: "integer", description: "Optional capture width." },
      height: { type: "integer", description: "Optional capture height." },
      format: { type: "string", enum: ["png", "jpg"], description: "Image format when not inferred from path." },
      inlineBase64: { type: "boolean", description: "Also return the encoded image as base64. Defaults false to avoid large output." }
    }, ["path"]),
    outputSchema: SCREENSHOT_OUTPUT,
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "supported",
    examples: [
      { description: "Capture the scene view to a PNG", argv: ["tools", "run", "screenshot.capture", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/CapturedShot.png\"}"] }
    ]
  },
  {
    name: "screenshot.game-view",
    summary: "Capture Game View output by rendering active cameras to an Assets/* image artifact.",
    description: "Capture the Game View by rendering all active cameras to an Assets/* image. Optionally include rendering diagnostics. Use this for runtime gameplay output; use screenshot.scene-view for the editor viewport. Default 1280x720.",
    group: "screenshot",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/* image path." },
      width: { type: "integer", default: 1280 },
      height: { type: "integer", default: 720 },
      overwrite: { type: "boolean", description: "Allow replacing an existing image." },
      includeDiagnostics: { type: "boolean", description: "Append rendering diagnostics to the result." },
      inlineBase64: { type: "boolean", description: "Also return the encoded image as base64. Defaults false to avoid large output." }
    }, ["path"]),
    outputSchema: SCREENSHOT_OUTPUT,
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "supported",
    examples: [
      { description: "Capture the game view with diagnostics", argv: ["tools", "run", "screenshot.game-view", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GameShot.png\",\"width\":1280,\"height\":720,\"includeDiagnostics\":true}"] }
    ]
  },
  {
    name: "screenshot.scene-view",
    summary: "Capture the Editor SceneView camera to an Assets/* image artifact.",
    description: "Capture the Editor SceneView camera to an Assets/* image. Use this for editor authoring output; in Play Mode prefer screenshot.game-view. Supports png/jpg via format.",
    group: "screenshot",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/* image path." },
      width: { type: "integer", description: "Optional capture width; defaults to SceneView size." },
      height: { type: "integer", description: "Optional capture height; defaults to SceneView size." },
      format: { type: "string", enum: ["png", "jpg"], default: "png" },
      overwrite: { type: "boolean", description: "Allow replacing an existing image." },
      inlineBase64: { type: "boolean", description: "Also return the encoded image as base64. Defaults false to avoid large output." }
    }, ["path"]),
    outputSchema: SCREENSHOT_OUTPUT,
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "supported",
    examples: [
      { description: "Capture the scene view", argv: ["tools", "run", "screenshot.scene-view", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/SceneShot.png\"}"] }
    ]
  },
  {
    name: "screenshot.isolated-object",
    summary: "Render one GameObject in isolation with a temporary camera to an Assets/* image artifact.",
    description: "Render a single GameObject in isolation with a temporary camera and save to an Assets/* image. Useful for clean asset previews. target resolves by name, hierarchy path or instance id. Orthographic (default) frames the object by size; perspective needs radius. Aligns with IvanMurzak isolated render.",
    group: "screenshot",
    inputSchema: objSchema({
      target: { type: "string", description: "GameObject to isolate: name, hierarchy path or instance id." },
      path: { type: "string", description: "Destination Assets/* image path." },
      width: { type: "integer", default: 512 },
      height: { type: "integer", default: 512 },
      orthographic: { type: "boolean", default: true, description: "Use an orthographic framing camera." },
      size: { type: "number", default: 2, description: "Orthographic frustum size around the object." },
      overwrite: { type: "boolean", description: "Allow replacing an existing image." },
      inlineBase64: { type: "boolean", description: "Also return the encoded image as base64. Defaults false to avoid large output." }
    }, ["target", "path"]),
    outputSchema: SCREENSHOT_OUTPUT,
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "editor-only",
    examples: [
      { description: "Isolate and render an object", argv: ["tools", "run", "screenshot.isolated-object", "--input", "{\"target\":\"AlignmentProbe\",\"path\":\"Assets/UnityCliAlignmentFixtures/IsoShot.png\",\"width\":512,\"height\":512}"] }
    ]
  },
  {
    name: "screenshot.surround",
    summary: "Capture six fixed angles around a GameObject; writes contact sheet and source PNGs, requires --yes.",
    description: "Capture six fixed angles around a target GameObject (front, back, left, right, top, bird_eye) and save a 3x2 contact sheet plus the six source PNGs. target resolves by name, hierarchy path or instance id. This writes image artifacts; use --dry-run to preview paths or --yes to execute. inlineBase64 returns the contact sheet as base64 only when explicitly requested.",
    group: "screenshot",
    inputSchema: objSchema({
      target: { type: "string", description: "GameObject to surround: name, hierarchy path or instance id." },
      "output-dir": { type: "string", description: "Assets/* directory for the generated six PNG shots. Defaults to Assets/Screenshots/Surround." },
      contactSheetPath: { type: "string", description: "Destination path for the generated 3x2 contact sheet PNG. Defaults under output-dir." },
      width: { type: "integer", default: 512, description: "Width of each source angle tile." },
      height: { type: "integer", default: 512, description: "Height of each source angle tile." },
      radius: { type: "number", description: "Camera radius from the target center. Defaults from renderer bounds." },
      fov: { type: "number", default: 60, description: "Temporary camera field of view in degrees." },
      overwrite: { type: "boolean", description: "Allow replacing existing shot/contact sheet files." },
      inlineBase64: { type: "boolean", description: "Also return the contact sheet as base64. Defaults false to avoid large output." }
    }, ["target"]),
    outputSchema: objSchema({
      command: { type: "string" },
      target: { type: "string" },
      count: { type: "integer" },
      contactSheet: { type: "object" },
      artifacts: { type: "array", items: { type: "object" } },
      imageBase64: { type: "string", description: "Present only when inlineBase64=true." }
    }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["artifact-write", "asset-write"],
    playModeSupport: "editor-only",
    examples: [
      { description: "Create a six-angle contact sheet", argv: ["tools", "run", "screenshot.surround", "--input", "{\"target\":\"AlignmentProbe\",\"output-dir\":\"Assets/UnityCliAlignmentFixtures/SurroundShots\",\"overwrite\":true}", "--yes"] }
    ]
  },
  {
    name: "screenshot.orbit",
    summary: "Capture multiple orbit screenshots around a GameObject and save each PNG under output-dir.",
    description: "Capture multiple screenshots orbiting a target GameObject at evenly spaced angles, saving each PNG under output-dir. target resolves by name, hierarchy path or instance id. Use this to generate turntable previews. Aligns with CoplayDev orbit/surround.",
    group: "screenshot",
    inputSchema: objSchema({
      target: { type: "string", description: "GameObject to orbit: name, hierarchy path or instance id." },
      "output-dir": { type: "string", description: "Assets/* directory for the generated PNG sequence." },
      count: { type: "integer", default: 8, description: "Number of orbit angles." },
      radius: { type: "number", default: 5, description: "Orbit radius from the target." },
      width: { type: "integer", default: 512 },
      height: { type: "integer", default: 512 }
    }, ["target"]),
    outputSchema: objSchema({
      command: { type: "string" },
      outputDir: { type: "string" },
      count: { type: "integer" },
      captured: { type: "array", items: objSchema({ path: { type: "string" }, sizeBytes: { type: "integer" } }) }
    }),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "editor-only",
    examples: [
      { description: "Orbit an object with 8 shots", argv: ["tools", "run", "screenshot.orbit", "--input", "{\"target\":\"AlignmentProbe\",\"output-dir\":\"Assets/UnityCliAlignmentFixtures/OrbitShots\",\"count\":8,\"radius\":5}"] }
    ]
  }
];
