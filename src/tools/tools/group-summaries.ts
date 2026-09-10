// 每个公开 Tool group 的路由级一句话摘要，供顶层 tools group/list 控制面复用。
// 覆盖当前全部公开组；新增组未登记时回退到组名兜底，避免空泛 capability 文案。

export const GROUP_SUMMARIES: Record<string, string> = {
  "animation-clip": "Animation clip assets: read, create, and update curves, events, and clip settings.",
  animator: "Animator playback, state, parameters, speed, and enablement on scene objects.",
  "animator-controller": "Animator controller layers, states, transitions, blend trees, and parameters.",
  asset: "Unity asset discovery, import, editing, and refresh operations.",
  build: "Build Settings scenes, platforms, profiles, player settings, and live builds with terminal artifact proof.",
  camera: "Camera creation, discovery, lens settings, targeting, and multiview capture.",
  cinemachine: "Cinemachine virtual camera, brain, aim, body, blend, noise, and extension configuration.",
  code: "C# code execution with explicit safety controls.",
  catalog: "Built-in Editor and development Player tool catalog transports: health, groups, summary, describe, list, and run per target.",
  component: "Add, get, list, modify, remove, and set components on scene objects.",
  console: "Unity Console reading and exact clear operations with count readback.",
  docs: "Offline documentation lookup and search.",
  editor: "Unity Editor state, transitions, dialogs, refresh, selection, focus, and session controls.",
  extension: "Inspect installed UnityCLI extensions and their contributed tools.",
  "game-object": "Create, find, parent, duplicate, transform, and delete scene GameObjects.",
  lighting: "Lightmapping bake, light probes, reflection probes, skybox, ambient, and fog settings.",
  material: "Create, inspect, assign, and edit material properties on assets and renderers.",
  navmesh: "NavMesh bake settings, baking, and baked data clearing.",
  occlusion: "Occlusion culling bake lifecycle: bake, status, cancel, and clearing baked data.",
  package: "Unity package mutation, resolve, embed, deploy, restore, and registry configuration with durable proof.",
  physics: "Physics raycasts with optional all hits, overlaps, shapes, simulation, collisions, materials, joints, and rigidbodies.",
  prefab: "Prefab creation, instantiation, editing, overrides, stages, and unpacking.",
  probuilder: "ProBuilder shape, mesh, face, edge, and repair operations.",
  profiler: "Profiler capture, analysis, memory, frame debugger, and performance stats.",
  "project-settings": "Project-wide tags, layers, audio, input, time, player identity, and general info.",
  rendering: "Render pipeline, quality settings, scene debug drawing, and unified render stats.",
  "runtime-reflection": "Discover and invoke live C# methods and type schemas via runtime reflection.",
  scene: "Scene hierarchy, one-GameObject reads, load, save, validation, and view operations.",
  screenshot: "Capture game view, scene view, isolated objects, and camera screenshots with artifact proof.",
  "scriptable-object": "Create, inspect, and modify ScriptableObject assets.",
  "serialized-object": "Read and patch serialized object data on assets and scene objects via verified paths.",
  shader: "Create, read, update, delete, and inspect shader assets.",
  test: "Filtered EditMode and PlayMode test execution, durable results, XML artifacts, and exact cancellation.",
  texture: "Create, modify, import, generate, and inspect textures and sprites.",
  timeline: "Create and edit Timeline assets, tracks, and clips.",
  "type-inspection": "Offline C# and Unity type schema, member, and search inspection over the reflection cache.",
  ui: "UI Toolkit documents, panels, styles, and visual element creation and inspection.",
  urp: "Universal Render Pipeline settings, renderer features, and volume effects.",
  vfx: "Visual Effect Graph control, effects, particles, and templates.",
  volume: "Scene Volume components and their rendered profile state.",
  "player-runtime": "Development Player state, console, capture, timing, and input operations.",
  UnityPipeline: "Official com.unity.pipeline commands projected from UnityCLI attributes.",
};

/** 返回组的 route summary；未登记组回退到组名兜底，避免空泛 capability 文案。 */
export function groupSummary(name: string): string {
  return GROUP_SUMMARIES[name] ?? `${name} operations.`;
}
