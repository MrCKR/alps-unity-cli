// 每个公开 Tool group 的路由级一句话摘要，供 tools.group 与 tools.group-list 复用。
// 覆盖当前全部公开组；新增组未登记时回退到组名兜底，避免空泛 capability 文案。

export const GROUP_SUMMARIES: Record<string, string> = {
  animation: "Animator state, parameters, and animation clip playback, editing, and creation.",
  asset: "Unity asset discovery, import, editing, and refresh operations.",
  build: "Build Settings scenes, platforms, profiles, player settings, and live builds with terminal artifact proof.",
  camera: "Camera creation, discovery, lens settings, targeting, and view capture.",
  cinemachine: "Cinemachine virtual camera, brain, aim, body, blend, noise, and extension configuration.",
  code: "C# code execution with explicit safety controls.",
  component: "Add, get, list, modify, remove, and set components on scene objects.",
  console: "Unity Console reading and exact clear operations with count readback.",
  controller: "Animator controller layers, states, transitions, blend trees, and parameters.",
  diagnose: "CLI, target, and Bridge capability diagnostics.",
  docs: "Offline documentation lookup and search.",
  editor: "Unity Editor state, transitions, dialogs, refresh, selection, focus, and session controls.",
  extension: "Inspect installed UnityCLI extensions and their contributed tools.",
  go: "Create, find, parent, duplicate, transform, and delete scene GameObjects.",
  graphics: "Dedicated rendering pipeline, quality, probe, unified stats, lighting, skybox, baking, and volume operations.",
  hdrp: "High Definition Render Pipeline settings and volume effect overrides.",
  material: "Create, inspect, assign, and edit material properties on assets and renderers.",
  navmesh: "NavMesh bake settings, baking, and baked data clearing.",
  object: "Read and modify serialized object data on assets and scene objects.",
  occlusion: "Clear baked occlusion culling data.",
  package: "Unity package mutation, resolve, embed, deploy, restore, and registry configuration with durable proof.",
  physics: "Physics raycasts with optional all hits, overlaps, shapes, simulation, collisions, materials, joints, and rigidbodies.",
  prefab: "Prefab creation, instantiation, editing, overrides, stages, and unpacking.",
  probuilder: "ProBuilder shape, mesh, face, edge, and repair operations.",
  profiler: "Profiler capture, analysis, memory, frame debugger, and performance stats.",
  project: "Project settings, layers, tags, audio, input, time, and player settings.",
  reflect: "Offline C# and Unity type reflection search.",
  reflection: "Invoke and discover C# methods and type schemas via runtime reflection.",
  runtime: "Development Player state, console, capture, timing, and input operations.",
  scene: "Scene hierarchy, one-GameObject reads, load, save, validation, and view operations.",
  screenshot: "Capture game view, scene view, and isolated object screenshots.",
  "scriptable-object": "Create, inspect, and modify ScriptableObject assets.",
  shader: "Create, read, update, delete, and inspect shader assets.",
  test: "Filtered EditMode and PlayMode test execution, durable results, XML artifacts, and exact cancellation.",
  texture: "Create, modify, import, generate, and inspect textures and sprites.",
  timeline: "Create and edit Timeline assets, tracks, and clips.",
  tools: "Tool catalog grouping, search, describe, and execution.",
  ui: "UI Toolkit documents, panels, styles, and visual element creation and inspection.",
  urp: "Universal Render Pipeline settings, renderer features, and volume effects.",
  vfx: "Visual Effect Graph control, effects, particles, and templates.",
  UnityPipeline: "Official com.unity.pipeline commands projected from UnityCLI attributes.",
};

/** 返回组的 route summary；未登记组回退到组名兜底，避免空泛 capability 文案。 */
export function groupSummary(name: string): string {
  return GROUP_SUMMARIES[name] ?? `${name} operations.`;
}
