import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const GO_TOOLS: ToolMetadata[] = [
  {
    name: "go.find",
    summary: "Search GameObjects by name, tag, layer, component, hierarchy path, or instance id; optionally prefer an open Prefab Stage.",
    description: "Search GameObjects by name, tag, layer, component, hierarchy path, or instance id. Results are lightweight and paged. Pass preferPrefabStage=true to first match inside an open Prefab Stage (prefabContentsRoot), then merge scene matches — useful while editing a prefab so its contents resolve before scene objects.",
    group: "go",
    inputSchema: objSchema({
      target: { type: "string", description: "Search term. Alias of name/searchTerm/query." },
      name: { type: "string", description: "Name search term. Kept for legacy calls." },
      searchTerm: { type: "string", description: "Explicit search term for --input JSON." },
      query: { type: "string", description: "Alias for searchTerm." },
      searchMethod: { type: "string", enum: ["by_name", "by_tag", "by_layer", "by_component", "by_path", "by_id"], default: "by_name", description: "How the search term is matched. Defaults to by_name; by_path accepts hierarchy paths." },
      includeInactive: { type: "boolean", default: false, description: "Include inactive scene objects in the search." },
      preferPrefabStage: { type: "boolean", default: false, description: "If true and a Prefab Stage is open, first match inside the prefab contents, then merge scene matches." },
      pageSize: { type: "integer", default: 50, maximum: 500, description: "Maximum matches returned in this page." },
      cursor: { type: "integer", default: 0, description: "0-based result offset for pagination." }
    }),
    outputSchema: pageSchema({ type: "object", properties: { id: { type: "integer" }, name: { type: "string" }, path: { type: "string" }, active: { type: "boolean" }, tag: { type: "string" }, layer: { type: "integer" }, componentCount: { type: "integer" }, components: { type: "array", items: { type: "string" } } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.find",
    docsTopic: "go",
    equivalentUnityMcp: "find_gameobjects / gameobject-find",
    examples: [
      { description: "Find by name", argv: ["tools", "run", "go.find", "--input", "{\"searchTerm\":\"Player\",\"searchMethod\":\"by_name\",\"pageSize\":25}"] },
      { description: "Find objects with Camera component", argv: ["tools", "run", "go.find", "--input", "{\"searchTerm\":\"Camera\",\"searchMethod\":\"by_component\",\"pageSize\":10}"] }
    ]
  },
  {
    name: "go.inspect",
    summary: "Inspect one GameObject by name, hierarchy path, or instance id.",
    description: "Inspect one GameObject by name, hierarchy path, or instance id. Returns transform, components and direct children; use object.get-data or component.get for deeper serialized data and keep go.find/scene.hierarchy for discovery.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject name, hierarchy path, or instance id." } }, ["target"]),
    outputSchema: objSchema({ gameObject: { type: "object" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.inspect",
    docsTopic: "go",
    equivalentUnityMcp: "mcpforunity://scene/gameobject/{id} / gameobject-find",
    examples: [
      { description: "Inspect by hierarchy path", argv: ["tools", "run", "go.inspect", "--input", "{\"target\":\"Player/CameraRig\"}"] }
    ]
  },
  {
    name: "go.create",
    summary: "Create a scene GameObject, optionally as a Unity primitive, parented under another object, and with simple transform/tag/layer/active values.",
    description: "Create a scene GameObject, optionally as a Unity primitive, parented under another object, and with simple transform/tag/layer/active values. When parenting under an object created earlier in the workflow, use the returned hierarchy path or instance id instead of a plain name. In Play Mode this changes only the current editor/runtime session unless persisted by normal Unity workflows.",
    group: "go",
    inputSchema: objSchema({
      name: { type: "string", default: "GameObject", description: "New GameObject name." },
      parent: { type: "string", description: "Optional parent hierarchy path or instance id; after creating a parent, prefer the returned path/id instead of a plain name." },
      primitive: { type: "string", enum: ["cube", "sphere", "capsule", "cylinder", "plane", "quad"], description: "Optional Unity primitive type to create." },
      active: { type: "boolean", description: "Initial activeSelf value." },
      tagValue: { type: "string", description: "Initial tag name." },
      layerValue: { type: "string", description: "Initial layer name or numeric index." },
      position: { type: "object", description: "Optional {x,y,z}; equivalent to x/y/z." },
      rotation: { type: "object", description: "Optional Euler {x,y,z}; equivalent to rotationX/Y/Z." },
      scale: { type: "object", description: "Optional {x,y,z}; equivalent to scaleX/Y/Z." },
      x: { type: "number" }, y: { type: "number" }, z: { type: "number" },
      rotationX: { type: "number" }, rotationY: { type: "number" }, rotationZ: { type: "number" },
      scaleX: { type: "number" }, scaleY: { type: "number" }, scaleZ: { type: "number" }
    }),
    outputSchema: objSchema({ gameObject: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "undo-stack"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.create",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.create / gameobject-create",
    examples: [
      { description: "Create a cube under root", argv: ["tools", "run", "go.create", "--input", "{\"name\":\"AlignmentCube\",\"primitive\":\"cube\",\"position\":{\"x\":0,\"y\":1,\"z\":0}}"] }
    ]
  },
  {
    name: "go.modify",
    summary: "Modify one or more GameObjects' name, active state, tag, layer, absolute transform values, or parent.",
    description: "Modify one or more GameObjects' name, active state, tag, layer, absolute transform values, or parent. Pass target for one object or targets[] to apply the same mutation to multiple objects in one all-or-none call; every target must resolve uniquely before any scene object is changed. Prefer hierarchy paths or instance ids for target, targets and parent after create or duplicate operations. For component fields use component.set/modify; for deep object data use object.modify.",
    group: "go",
    inputSchema: objSchema({
      target: { type: "string", description: "Single GameObject hierarchy path or instance id; a plain name is allowed only when unique. Use either target or targets." },
      targets: { type: "array", items: { type: "string" }, description: "Multiple GameObject hierarchy paths or instance ids. The same mutation is applied to each target after all targets resolve uniquely." },
      newName: { type: "string", description: "Rename target GameObject. Avoid using newName with targets[] unless duplicate names are intentional." },
      parent: { type: "string", description: "Optional parent hierarchy path or instance id; a plain name is allowed only when unique." },
      active: { type: "boolean" },
      tagValue: { type: "string" },
      layerValue: { type: "string" },
      position: { type: "object", description: "Optional absolute {x,y,z}." },
      rotation: { type: "object", description: "Optional Euler {x,y,z}." },
      scale: { type: "object", description: "Optional {x,y,z}." },
      x: { type: "number" }, y: { type: "number" }, z: { type: "number" },
      rotationX: { type: "number" }, rotationY: { type: "number" }, rotationZ: { type: "number" },
      scaleX: { type: "number" }, scaleY: { type: "number" }, scaleZ: { type: "number" }
    }),
    outputSchema: objSchema({ gameObject: { type: "object", description: "Present for single target calls." }, gameObjects: { type: "array", items: { type: "object" }, description: "Present for targets[] batch calls." }, modifiedCount: { type: "integer" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "undo-stack"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.modify",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.modify / gameobject-modify",
    examples: [
      { description: "Move and deactivate by hierarchy path", argv: ["tools", "run", "go.modify", "--input", "{\"target\":\"Player/Avatar\",\"position\":{\"x\":1,\"y\":2,\"z\":3},\"active\":false}"] },
      { description: "Apply the same transform to multiple objects", argv: ["tools", "run", "go.modify", "--input", "{\"targets\":[\"Generated/A\",\"Generated/B\"],\"position\":{\"x\":0,\"y\":1,\"z\":0}}"] }
    ]
  },
  {
    name: "go.duplicate",
    summary: "Duplicate a GameObject in the active scene, optionally assigning a new name or parent.",
    description: "Duplicate a GameObject in the active scene, optionally assigning a new name or parent. Prefer hierarchy paths or instance ids for target and parent; returns the duplicate path/id so follow-up calls can avoid ambiguous names.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string", description: "Source GameObject hierarchy path or instance id; a plain name is allowed only when unique." }, name: { type: "string", description: "New duplicate name alias." }, newName: { type: "string", description: "New duplicate name." }, parent: { type: "string", description: "Optional parent hierarchy path or instance id; a plain name is allowed only when unique." } }, ["target"]),
    outputSchema: objSchema({ gameObject: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "undo-stack"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.duplicate",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.duplicate / gameobject-duplicate",
    examples: [
      { description: "Duplicate by hierarchy path", argv: ["tools", "run", "go.duplicate", "--input", "{\"target\":\"Player/Avatar\",\"newName\":\"Player Copy\"}"] }
    ]
  },
  {
    name: "go.move-relative",
    summary: "Move a GameObject by a relative XYZ offset without replacing its absolute transform.",
    description: "Move a GameObject by a relative XYZ offset without replacing its absolute transform. Use go.modify when you need absolute position; prefer hierarchy paths or instance ids after create/duplicate operations.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject hierarchy path or instance id; a plain name is allowed only when unique." }, offset: { type: "object", description: "Optional {x,y,z}; equivalent to offsetX/Y/Z." }, offsetX: { type: "number" }, offsetY: { type: "number" }, offsetZ: { type: "number" } }, ["target"]),
    outputSchema: objSchema({ gameObject: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "undo-stack"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.move-relative",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.move_relative",
    examples: [
      { description: "Move up by hierarchy path", argv: ["tools", "run", "go.move-relative", "--input", "{\"target\":\"Player/Avatar\",\"offset\":{\"x\":0,\"y\":2,\"z\":0}}"] }
    ]
  },
  {
    name: "go.look-at",
    summary: "Rotate a GameObject so its forward vector looks at another GameObject.",
    description: "Rotate a GameObject so its forward vector looks at another GameObject. Prefer hierarchy paths or instance ids for both target and lookAt after create/duplicate operations. This changes Transform rotation in the current scene/session.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string", description: "Object to rotate, preferably a hierarchy path or instance id." }, lookAt: { type: "string", description: "Destination GameObject hierarchy path or instance id; a plain name is allowed only when unique." }, destination: { type: "string", description: "Alias for lookAt." } }, ["target", "lookAt"]),
    outputSchema: objSchema({ gameObject: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "undo-stack"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.look-at",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.look_at",
    examples: [
      { description: "Look at another object by hierarchy path", argv: ["tools", "run", "go.look-at", "--input", "{\"target\":\"CameraRig/MainCamera\",\"lookAt\":\"Player/Avatar\"}"] }
    ]
  },
  {
    name: "go.delete",
    summary: "Delete one GameObject from the scene.",
    description: "Delete one GameObject from the scene. This recursively removes its children in the current scene/session. Prefer hierarchy paths or instance ids, especially after create/duplicate operations where plain names may be ambiguous.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject hierarchy path or instance id; a plain name is allowed only when unique." } }, ["target"]),
    outputSchema: objSchema({ deleted: { type: "object" }, target: { type: "object", description: "Present for dry-run previews before deletion." }, dryRun: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "undo-stack", "recursive-delete"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.delete",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.delete / gameobject-destroy",
    examples: [
      { description: "Dry-run deletion by hierarchy path", argv: ["tools", "run", "go.delete", "--dry-run", "--input", "{\"target\":\"TempRoot/GeneratedChild\"}"] },
      { description: "Delete by instance id", argv: ["tools", "run", "go.delete", "--input", "{\"target\":\"12345\"}"] }
    ]
  },
  {
    name: "go.set-parent",
    summary: "Reparent a GameObject under another GameObject or detach it to the scene root.",
    description: "Reparent a GameObject under another GameObject or detach it to the scene root. Prefer hierarchy paths or instance ids for target and parent; plain names can be ambiguous after duplicate/create operations. Use --dry-run first for hierarchy work; worldPositionStays defaults to true.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string", description: "Child GameObject hierarchy path or instance id; a plain name is allowed only when unique." }, parent: { type: "string", description: "Parent hierarchy path or instance id; omit to detach to root. Plain names are allowed only when unique." }, worldPositionStays: { type: "boolean", default: true } }, ["target"]),
    outputSchema: objSchema({ target: { type: "object" }, parent: { type: "string" }, worldPositionStays: { type: "boolean" }, dryRun: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "undo-stack", "hierarchy-reparent"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run go.set-parent",
    docsTopic: "go",
    equivalentUnityMcp: "manage_gameobject.modify parent / gameobject-set-parent",
    examples: [
      { description: "Dry-run reparent by hierarchy path", argv: ["tools", "run", "go.set-parent", "--dry-run", "--input", "{\"target\":\"Player/Sword\",\"parent\":\"Player/RightHand\",\"worldPositionStays\":true}"] },
      { description: "Detach to root by hierarchy path", argv: ["tools", "run", "go.set-parent", "--input", "{\"target\":\"Player/Sword\"}"] }
    ]
  }
];

export const COMPONENT_TOOLS: ToolMetadata[] = [
  {
    name: "component.add",
    summary: "Add a Unity Component to a GameObject, optionally setting simple public fields/properties from a properties object after the component is created.",
    description: "Add a Unity Component to a GameObject, optionally setting simple public fields/properties from a properties object after the component is created. Use component.list-types first when the exact type name is unclear. In Play Mode, scene changes affect the current session and should not be assumed persistent.",
    group: "component",
    inputSchema: objSchema({
      target: { type: "string", description: "GameObject name, hierarchy path, or instance id." },
      component: { type: "string", description: "Component type name, e.g. Rigidbody, BoxCollider, UnityEngine.Light, or a custom MonoBehaviour full name." },
      properties: { type: "object", description: "Optional simple public fields/properties to set immediately after add, e.g. {\"mass\":2,\"useGravity\":false}. Use component.modify for nested/object-reference patches." }
    }, ["target", "component"]),
    outputSchema: objSchema({ target: { type: "object" }, component: { type: "object" }, changedProperties: { type: "array", items: { type: "object" } }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["component-add", "scene-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.add",
    docsTopic: "component",
    equivalentUnityMcp: "manage_components.add / gameobject-component-add",
    examples: [
      { description: "Add Rigidbody with initial properties", argv: ["tools", "run", "component.add", "--input", "{\"target\":\"Player\",\"component\":\"Rigidbody\",\"properties\":{\"mass\":2,\"useGravity\":false}}"] }
    ]
  },
  {
    name: "component.remove",
    summary: "Remove one or more non-Transform Components from a GameObject.",
    description: "Remove one or more non-Transform Components from a GameObject in the current scene/session. Use component for one type and componentIndex when multiple components of the same type exist; use components as a string array to remove multiple different component types in one call. Inspect components first with context read scene.gameobject.components or component.get.",
    group: "component",
    inputSchema: objSchema({
      target: { type: "string", description: "GameObject name, hierarchy path, or instance id." },
      component: { type: "string", description: "Component type name to remove for single-component mode." },
      components: { type: "array", items: { type: "string" }, description: "Multiple component type names to remove from the same target. Does not support per-type componentIndex; use single mode for duplicate components." },
      componentIndex: { type: "integer", description: "Optional zero-based index among components of the requested type in single-component mode." }
    }, ["target"]),
    outputSchema: objSchema({ target: { type: "object" }, component: { type: "object" }, components: { type: "array", items: { type: "object" } }, removedCount: { type: "integer" }, componentIndex: { type: ["integer", "null"] }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["component-remove", "scene-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.remove",
    docsTopic: "component",
    equivalentUnityMcp: "manage_components.remove / gameobject-component-destroy",
    examples: [
      { description: "Remove first BoxCollider", argv: ["tools", "run", "component.remove", "--input", "{\"target\":\"Player\",\"component\":\"BoxCollider\",\"componentIndex\":0}"] },
      { description: "Remove multiple component types", argv: ["tools", "run", "component.remove", "--input", "{\"target\":\"Player\",\"components\":[\"BoxCollider\",\"Rigidbody\"]}"] }
    ]
  },
  {
    name: "component.set",
    summary: "Set one simple public field/property, or multiple simple fields/properties via properties, on a Component attached to a GameObject.",
    description: "Set one simple public field/property, or multiple simple fields/properties via properties, on a Component attached to a GameObject. For nested values, object references, arrays, or SerializedObject-style patches, use component.modify after component.get/type.schema. In Play Mode, scene component changes are runtime/session state unless later persisted by Unity workflow.",
    group: "component",
    inputSchema: objSchema({
      target: { type: "string", description: "GameObject name, hierarchy path, or instance id." },
      component: { type: "string", description: "Component type name to modify." },
      componentIndex: { type: "integer", description: "Optional zero-based index among components of the requested type." },
      property: { type: "string", description: "Single writable public field/property name." },
      value: { description: "Single property value. Prefer --input JSON so booleans and numbers keep their type." },
      properties: { type: "object", description: "Multiple simple property values, e.g. {\"mass\":5,\"useGravity\":false}." }
    }, ["target", "component"]),
    outputSchema: objSchema({ target: { type: "object" }, component: { type: "object" }, changedProperties: { type: "array", items: { type: "object" } }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["component-modify", "scene-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.set",
    docsTopic: "component",
    equivalentUnityMcp: "manage_components.set_property",
    examples: [
      { description: "Set multiple Rigidbody values", argv: ["tools", "run", "component.set", "--input", "{\"target\":\"Player\",\"component\":\"Rigidbody\",\"properties\":{\"mass\":5,\"useGravity\":false}}"] },
      { description: "Set one boolean value", argv: ["tools", "run", "component.set", "--input", "{\"target\":\"Main Camera\",\"component\":\"Camera\",\"property\":\"enabled\",\"value\":false}"] }
    ]
  },
  {
    name: "component.get",
    summary: "Deep-read one Component after selecting a GameObject with go.find/go.inspect or context read scene.gameobject.components.",
    description: "Deep-read one Component after selecting a GameObject with go.find/go.inspect or context read scene.gameobject.components. For token safety, pass exact paths or one viewQuery; they are mutually exclusive and skip full fields/props expansion. Use componentIndex/componentInstanceId when duplicate components exist. Keep maxDepth low before expanding MonoBehaviours.",
    group: "component",
    inputSchema: objSchema({
      instanceId: { type: "integer", description: "GameObject instance ID." },
      path: { type: "string", description: "GameObject hierarchy path or name." },
      componentInstanceId: { type: "integer", description: "Component instance ID (highest priority)." },
      componentIndex: { type: "integer", description: "Component index on the GameObject." },
      component: { type: "string", description: "Component type name." },
      maxDepth: { type: "integer", default: 4, description: "Maximum serialization depth for full reads or viewQuery subtree." },
      includeFields: { type: "boolean", default: true },
      includeProperties: { type: "boolean", default: true },
      deepSerialization: { type: "boolean", default: true },
      paths: { type: "array", items: { type: "string" }, description: "Exact member paths to read as a small aggregate, e.g. [\"position\"] for Transform. Mutually exclusive with viewQuery." },
      viewQuery: { type: "object", description: "Optional subtree/filter read: {path?, namePattern?, typeFilter?, maxDepth?}. Mutually exclusive with paths." },
      namePattern: { type: "string", description: "Comma-separated exact member-name filter for full reads; prefer paths/viewQuery for token safety." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      typeName: { type: "string" },
      componentInstanceId: { type: "integer" },
      gameObject: { type: "object" },
      data: { type: "object", description: "SerializedMember tree or UnityCli.PathReadAggregate when paths is supplied." },
      logPath: { type: "string" }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.get",
    docsTopic: "component",
    equivalentUnityMcp: "gameobject-component-get",
    examples: [
      { description: "Read Transform position only", argv: ["tools", "run", "component.get", "--input", "{\"path\":\"Main Camera\",\"component\":\"Transform\",\"paths\":[\"position\"],\"maxDepth\":2}"] },
      { description: "Read one filtered subtree", argv: ["tools", "run", "component.get", "--input", "{\"path\":\"Main Camera\",\"component\":\"Transform\",\"viewQuery\":{\"namePattern\":\"position\",\"maxDepth\":1}}"] }
    ]
  },
  {
    name: "component.modify",
    summary: "Apply pathPatches, jsonPatch (Merge Patch) or componentDiff to a Component.",
    description: "Apply pathPatches, jsonPatch (Merge Patch) or componentDiff to a Component. Returns updated data, changed paths, logs and audit. Use component.get first. In Play Mode, scene component changes affect only the current runtime session unless later persisted by Unity workflow; in Edit Mode they mark scene/component state dirty.",
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
    destructiveHint: false,
    playModeSupport: "session-affecting",
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
    summary: "Enumerate Component types available in the current project across loaded assemblies.",
    description: "Enumerate Component types available in the current project across loaded assemblies. Use keyword plus pageSize/cursor to bound token usage before calling component.add/get/set/modify. Returns fullName/name/assembly/namespace and pageInfo.nextCursor.",
    group: "component",
    inputSchema: objSchema({ keyword: { type: "string", description: "Optional name/namespace filter." }, pageSize: { type: "integer", default: 100, maximum: 500 }, cursor: { type: "integer", default: 0 } }),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, types: { type: "array", items: { type: "object", properties: { fullName: { type: "string" }, name: { type: "string" }, assembly: { type: "string" }, namespace: { type: ["string", "null"] } } } }, pageInfo: pageSchema }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run component.list-types",
    docsTopic: "component",
    equivalentUnityMcp: "gameobject-component-list-all",
    examples: [
      { description: "List Rigidbody types", argv: ["tools", "run", "component.list-types", "--input", "{\"keyword\":\"Rigidbody\",\"pageSize\":20}"] }
    ]
  }
];

