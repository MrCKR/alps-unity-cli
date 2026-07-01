import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

const cameraSchema = {
  id: { type: "integer", description: "Camera component instance id." },
  name: { type: "string" },
  path: { type: "string", description: "Scene hierarchy path." },
  tag: { type: "string" },
  enabled: { type: "boolean" },
  clearFlags: { type: "string" },
  projection: { type: "string", enum: ["perspective", "orthographic"] },
  fieldOfView: { type: "number" },
  orthographicSize: { type: "number" },
  depth: { type: "number" },
  pixelWidth: { type: "integer" },
  pixelHeight: { type: "integer" }
};

const artifactSchema = {
  screenshotPath: { type: "string" },
  artifactPath: { type: "string" },
  format: { type: "string", enum: ["png", "jpg"] },
  width: { type: "integer" },
  height: { type: "integer" },
  transparent: { type: "boolean" },
  quality: { type: "integer" },
  source: { type: "string" },
  target: { type: "string" }
};

export const CAMERA_TOOLS: ToolMetadata[] = [
  {
    name: "camera.list",
    summary: "List active scene Unity Camera components with name, hierarchy path, projection, FOV/ortho size, depth, targetTexture, and pixel size.",
    description: "List active scene Unity Camera components with name, hierarchy path, projection, FOV/ortho size, depth, targetTexture, and pixel size. Read-only; use paths or ids from this output for camera.info/screenshot/lens/target.",
    group: "camera",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ cameras: { type: "array", items: objSchema(cameraSchema) }, count: { type: "integer" } }, ["cameras", "count"]),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List scene cameras", argv: ["tools", "run", "camera.list", "--input", "{}"] }
    ]
  },
  {
    name: "camera.info",
    summary: "Get one scene Camera's serializable settings by name, path, or instance id.",
    description: "Get one Camera's serializable settings. Resolves by Camera name, hierarchy path, or instance id; returns E_CAMERA_NOT_FOUND with recovery guidance when missing.",
    group: "camera",
    inputSchema: objSchema({ target: { type: "string", description: "Camera name, hierarchy path, or instance id from camera.list." } }, ["target"]),
    outputSchema: objSchema({ camera: objSchema(cameraSchema) }, ["camera"]),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Inspect Main Camera", argv: ["tools", "run", "camera.info", "--input", "{\"target\":\"Main Camera\"}"] }
    ]
  },
  {
    name: "camera.screenshot",
    summary: "Render a PNG/JPG artifact from one specific Camera; use screenshot.* for view-based captures.",
    description: "Render a screenshot from a specific Camera into a PNG/JPG artifact file. Defaults return artifact paths only; inlineBase64 must be explicit and is omitted with a warning when the image exceeds the bridge inline limit. Restores camera render state after capture.",
    group: "camera",
    inputSchema: objSchema({ target: { type: "string", description: "Camera name, hierarchy path, or instance id." }, path: { type: "string", description: "File path for the artifact; relative paths resolve under the Unity project root." }, output: { type: "string", description: "Alias of path for CLI compatibility." }, width: { type: "integer", default: 1280, description: "Output width in pixels, clamped by the bridge for safety." }, height: { type: "integer", default: 720, description: "Output height in pixels, clamped by the bridge for safety." }, format: { type: "string", enum: ["png", "jpg", "jpeg"], description: "Defaults from output/path extension; jpeg is normalized to jpg." }, transparent: { type: "boolean", description: "Use transparent solid-color background for PNG-style captures." }, quality: { type: "integer", description: "JPG quality, clamped to 1..100." }, overwrite: { type: "boolean", description: "Allow replacing an existing output file." }, inlineBase64: { type: "boolean", default: false, description: "Also return imageBase64 when the encoded artifact is below the bridge inline limit." } }, ["target", "path"]),
    outputSchema: objSchema(artifactSchema, ["screenshotPath", "artifactPath", "format", "width", "height", "source", "target"]),
    destructiveHint: false,
    sideEffects: ["artifact-write", "render-texture"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Capture from Main Camera", argv: ["tools", "run", "camera.screenshot", "--input", "{\"target\":\"Main Camera\",\"path\":\"Artifacts/camera-main.png\",\"width\":640,\"height\":360,\"overwrite\":true}"] }
    ]
  },
  {
    name: "camera.create",
    summary: "Create a Unity Camera GameObject in the active scene with optional parent, transform, projection, clear flags, depth, and MainCamera tag.",
    description: "Create a Unity Camera GameObject in the active scene with optional parent, transform, projection, clear flags, depth, and MainCamera tag. Parent and enum inputs are validated before mutation to avoid partial objects.",
    group: "camera",
    inputSchema: objSchema({
      name: { type: "string", description: "New Camera GameObject name; defaults to Camera." },
      parent: { type: "string", description: "Optional existing parent GameObject name, path, or id." },
      position: { type: "string", description: "World position as x,y,z." },
      rotation: { type: "string", description: "Euler rotation as x,y,z." },
      fieldOfView: { type: "number", description: "Perspective field of view." },
      orthographic: { type: "boolean", description: "Whether to use orthographic projection." },
      orthographicSize: { type: "number", description: "Orthographic size when orthographic=true." },
      clearFlags: { type: "string", enum: ["Skybox", "SolidColor", "Depth", "Nothing"], description: "Unity CameraClearFlags enum value." },
      depth: { type: "number", description: "Camera rendering depth." },
      tag: { type: "string", enum: ["MainCamera"], description: "Only MainCamera is applied by this tool." }
    }),
    outputSchema: objSchema({ camera: objSchema(cameraSchema), plannedEffects: { type: "array" }, logPath: { type: "string" } }, ["camera"]),
    destructiveHint: false,
    sideEffects: ["scene-modify", "component-add"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Create a perspective camera", argv: ["tools", "run", "camera.create", "--input", "{\"name\":\"UnityCliAlignmentCamera\",\"position\":\"0,2,-6\",\"rotation\":\"15,0,0\",\"fieldOfView\":60}"] },
      { description: "Create an orthographic camera", argv: ["tools", "run", "camera.create", "--input", "{\"name\":\"UnityCliAlignmentTopView\",\"orthographic\":true,\"orthographicSize\":5,\"clearFlags\":\"SolidColor\"}"] }
    ]
  },
  {
    name: "camera.target",
    summary: "Aim or move one Camera transform using lookAt and/or explicit position/rotation.",
    description: "Set a Camera transform by looking at a GameObject and/or assigning explicit position/rotation. Session-affecting: marks the Camera dirty and can affect the running Play Mode view without persisting runtime-only state.",
    group: "camera",
    inputSchema: objSchema({
      target: { type: "string", description: "Camera name, hierarchy path, or instance id." },
      lookAt: { type: "string", description: "GameObject name, hierarchy path, or instance id to LookAt." },
      position: { type: "string", description: "World position as x,y,z." },
      rotation: { type: "string", description: "Euler rotation as x,y,z." }
    }, ["target"]),
    outputSchema: objSchema({ camera: objSchema(cameraSchema), plannedEffects: { type: "array" }, logPath: { type: "string" } }, ["camera"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Point a camera at a target", argv: ["tools", "run", "camera.target", "--input", "{\"target\":\"UnityCliAlignmentCamera\",\"lookAt\":\"UnityCliAlignmentProbe\"}"] },
      { description: "Move and rotate a camera", argv: ["tools", "run", "camera.target", "--input", "{\"target\":\"UnityCliAlignmentCamera\",\"position\":\"0,3,-8\",\"rotation\":\"20,0,0\"}"] }
    ]
  },
  {
    name: "camera.lens",
    summary: "Update Camera lens/projection settings: FOV, orthographic mode/size, near/far clip planes, clear flags, and depth.",
    description: "Update Camera lens/projection settings: FOV, orthographic mode/size, near/far clip planes, clear flags, and depth. Invalid clearFlags returns E_CAMERA_CLEAR_FLAGS_INVALID instead of silently doing nothing.",
    group: "camera",
    inputSchema: objSchema({
      target: { type: "string", description: "Camera name, hierarchy path, or instance id." },
      fieldOfView: { type: "number", description: "Perspective field of view." },
      orthographic: { type: "boolean", description: "Whether to use orthographic projection." },
      orthographicSize: { type: "number", description: "Orthographic size." },
      nearClipPlane: { type: "number", description: "Near clipping plane." },
      farClipPlane: { type: "number", description: "Far clipping plane." },
      clearFlags: { type: "string", enum: ["Skybox", "SolidColor", "Depth", "Nothing"], description: "Unity CameraClearFlags enum value." },
      depth: { type: "number", description: "Camera rendering depth." }
    }, ["target"]),
    outputSchema: objSchema({ camera: objSchema(cameraSchema), plannedEffects: { type: "array" }, logPath: { type: "string" } }, ["camera"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Set camera FOV", argv: ["tools", "run", "camera.lens", "--input", "{\"target\":\"UnityCliAlignmentCamera\",\"fieldOfView\":45,\"nearClipPlane\":0.1,\"farClipPlane\":1000}"] },
      { description: "Switch to orthographic", argv: ["tools", "run", "camera.lens", "--input", "{\"target\":\"UnityCliAlignmentCamera\",\"orthographic\":true,\"orthographicSize\":10}"] }
    ]
  },
  {
    name: "camera.multiview",
    summary: "Capture one PNG/JPG artifact per named Camera into an output directory for multiview review.",
    description: "Capture one PNG/JPG artifact per named Camera into an output directory. This is a file-artifact variant of CoplayDev's multiview/contact-sheet idea; keep target count and resolution small for token and disk efficiency.",
    group: "camera",
    inputSchema: objSchema({
      targets: { type: "string", description: "Comma-separated camera names, hierarchy paths, or instance ids." },
      output: { type: "string", default: "Screenshots", description: "Output directory; relative paths resolve under the Unity project root." },
      width: { type: "integer", default: 1280, description: "Per-camera output width." },
      height: { type: "integer", default: 720, description: "Per-camera output height." },
      format: { type: "string", enum: ["png", "jpg", "jpeg"], description: "Image format; defaults to png." }
    }, ["targets"]),
    outputSchema: objSchema({ captures: { type: "array", items: objSchema(artifactSchema) }, count: { type: "integer" }, plannedEffects: { type: "array" }, logPath: { type: "string" } }, ["captures", "count"]),
    destructiveHint: false,
    sideEffects: ["artifact-write", "render-texture"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Capture from two cameras", argv: ["tools", "run", "camera.multiview", "--input", "{\"targets\":\"Main Camera,UnityCliAlignmentCamera\",\"output\":\"Artifacts/camera-multiview\",\"width\":320,\"height\":180,\"format\":\"png\"}"] }
    ]
  }
];
