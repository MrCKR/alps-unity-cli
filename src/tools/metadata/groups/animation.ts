import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

const CLIP_WRAP_MODE_ENUM = ["Default", "Once", "Loop", "PingPong", "ClampForever"];
const CLIP_SETTING_INPUT = {
  frameRate: { type: "number", default: 60, description: "Clip frame rate. Values <= 0 are rejected." },
  legacy: { type: "boolean", default: false, description: "Set AnimationClip.legacy." },
  wrapMode: { type: "string", enum: CLIP_WRAP_MODE_ENUM, description: "AnimationClip wrap mode for legacy playback. Unity treats Once and Clamp as the same enum value and may report either name when read back." },
  loopTime: { type: "boolean", description: "Set AnimationClipSettings.loopTime." },
  startTime: { type: "number", description: "AnimationClipSettings.startTime in seconds." },
  stopTime: { type: "number", description: "AnimationClipSettings.stopTime in seconds. Must be >= startTime." },
  length: { type: "number", description: "Duration in seconds; when provided without stopTime, stopTime becomes startTime + length." },
  maxCurveKeys: { type: "integer", default: 5, description: "Maximum keyframes returned per curve binding in the response." }
};
const ANIMATION_CLIP_OUTPUT = objSchema({
  clip: objSchema({
    path: { type: "string" },
    guid: { type: "string" },
    name: { type: "string" },
    length: { type: "number" },
    frameRate: { type: "number" },
    legacy: { type: "boolean" },
    wrapMode: { type: "string" },
    localBounds: { type: "object" },
    settings: { type: "object", description: "AnimationClipSettings summary, including loopTime/startTime/stopTime and loop blend flags." },
    bindings: { type: "object", description: "Float curve bindings with bounded key summaries controlled by maxCurveKeys." },
    objectReferenceBindings: { type: "object", description: "Object reference curve bindings with bounded key summaries controlled by maxCurveKeys." },
    events: { type: "object", description: "AnimationEvent count and event summaries." }
  }),
  plannedEffects: { type: "array", items: { type: "object" } },
  logPath: { type: "string" }
});

export const ANIMATION_TOOLS: ToolMetadata[] = [
  {
    name: "animation.clip",
    summary: "Read an AnimationClip asset with clip settings, events, and bounded curve key summaries.",
    description: "Read an existing Assets/*.anim AnimationClip. Returns frameRate, legacy, wrapMode, localBounds, AnimationClipSettings, AnimationEvents, float curve bindings, object-reference curve bindings, and bounded key summaries. Use maxCurveKeys to control response size. Use animation.clip.create for persistent .anim creation or overwrite.",
    group: "animation",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.anim AnimationClip path." },
      maxCurveKeys: { type: "integer", default: 5, description: "Maximum keyframes returned per curve binding. Use 0 to omit key details." }
    }, ["path"]),
    outputSchema: ANIMATION_CLIP_OUTPUT,
    destructiveHint: false,
    playModeSupport: "supported",
    equivalentUnityMcp: "manage_animation.clip_get_info",
    handler: "bridge",
    cliCommand: "tools run animation.clip",
    docsTopic: "animation",
    examples: [
      { description: "Read clip settings and bounded curve keys", argv: ["tools", "run", "animation.clip", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentAnimationProbe.anim\",\"maxCurveKeys\":3}"] }
    ]
  },
  {
    name: "animation.clip.create",
    summary: "Create or overwrite an Assets/*.anim AnimationClip with clip-level properties.",
    description: "Create or overwrite an Assets/*.anim AnimationClip and set clip-level properties: frameRate, legacy, wrapMode, loopTime, startTime, stopTime, or length. This writes a persistent .anim asset and refreshes AssetDatabase state, so it requires --yes unless --dry-run is used. Curve and event editing remain separate future tools; this tool only creates the clip shell and metadata-level settings.",
    group: "animation",
    inputSchema: objSchema({
      path: { type: "string", description: "Destination Assets/*.anim AnimationClip path." },
      overwrite: { type: "boolean", description: "Allow replacing an existing clip asset." },
      ...CLIP_SETTING_INPUT
    }, ["path"]),
    outputSchema: ANIMATION_CLIP_OUTPUT,
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.clip_create",
    handler: "bridge",
    cliCommand: "tools run animation.clip.create",
    docsTopic: "animation",
    examples: [
      { description: "Preview creating a looping clip", argv: ["tools", "run", "animation.clip.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/NewClip.anim\",\"frameRate\":30,\"length\":1,\"loopTime\":true,\"wrapMode\":\"Loop\"}", "--dry-run"] },
      { description: "Create a looping clip after confirmation", argv: ["tools", "run", "animation.clip.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/NewClip.anim\",\"frameRate\":30,\"length\":1,\"loopTime\":true,\"wrapMode\":\"Loop\"}", "--yes"] }
    ]
  },
  {
    name: "animation.clip.modify",
    summary: "Modify clip-level properties on an existing Assets/*.anim AnimationClip.",
    description: "Modify clip-level properties on an existing Assets/*.anim AnimationClip: frameRate, legacy, wrapMode, loopTime, startTime, stopTime, or length. This is a persistent .anim asset write and requires --yes unless --dry-run is used. It does not edit animation curves or events; use animation.clip first to inspect current bindings and events.",
    group: "animation",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.anim AnimationClip path." },
      ...CLIP_SETTING_INPUT
    }, ["path"]),
    outputSchema: ANIMATION_CLIP_OUTPUT,
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.clip_set_properties",
    handler: "bridge",
    cliCommand: "tools run animation.clip.modify",
    docsTopic: "animation",
    examples: [
      { description: "Preview changing loop settings", argv: ["tools", "run", "animation.clip.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentAnimationProbe.anim\",\"loopTime\":true,\"wrapMode\":\"Loop\"}", "--dry-run"] },
      { description: "Apply clip-level settings", argv: ["tools", "run", "animation.clip.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentAnimationProbe.anim\",\"loopTime\":true,\"wrapMode\":\"Loop\"}", "--yes"] }
    ]
  },
  {
    name: "animation.state",
    summary: "Query the current Animator state on a target GameObject.",
    description: "Query the current Animator state on a target GameObject. Read-only; use animator.get-info when parameters, layers, and clips are also needed.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" }, layer: { type: "integer" } }, ["target"]),
    destructiveHint: false,
    playModeSupport: "supported",
    equivalentUnityMcp: "manage_animation.animator_get_info",
    handler: "bridge",
    cliCommand: "tools run animation.state",
    docsTopic: "animation",
    examples: [
      { description: "Read current Animator state", argv: ["tools", "run", "animation.state", "--input", "{\"target\":\"UnityCliAlignmentProbe\",\"layer\":0}"] }
    ]
  },
  {
    name: "animation.play",
    summary: "Play an animation state on a target GameObject's Animator.",
    description: "Play an animation state on a target GameObject's Animator. Session-affecting: changes the current Animator state; in Play Mode this affects only the running session, while edit-mode calls can mark the Animator dirty.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" }, state: { type: "string" }, layer: { type: "integer" }, normalizedTime: { type: "number" } }, ["target", "state"]),
    destructiveHint: false,
    sideEffects: ["animation-state", "scene-modify"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.animator_play",
    handler: "bridge",
    cliCommand: "tools run animation.play",
    docsTopic: "animation",
    examples: [
      { description: "Play an Animator state", argv: ["tools", "run", "animation.play", "--input", "{\"target\":\"UnityCliAlignmentProbe\",\"state\":\"Base Layer.Idle\",\"layer\":0}"] }
    ]
  },
  {
    name: "animation.stop",
    summary: "Stop/reset the current Animator playback on a target.",
    description: "Stop/reset the current Animator playback on a target. Session-affecting: Rebind/Update changes Animator runtime state and may mark the component dirty in edit mode.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    destructiveHint: false,
    sideEffects: ["animation-state", "scene-modify"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.animator_*",
    handler: "bridge",
    cliCommand: "tools run animation.stop",
    docsTopic: "animation",
    examples: [
      { description: "Reset Animator playback", argv: ["tools", "run", "animation.stop", "--input", "{\"target\":\"UnityCliAlignmentProbe\"}"] }
    ]
  },
  {
    name: "animator.get-info",
    summary: "Get comprehensive Animator component info including enabled state, speed, controller, parameters, layers, and clips.",
    description: "Get comprehensive Animator component info including enabled state, speed, controller, parameters, layers, and clips. Read-only; returns E_ANIMATOR_NOT_FOUND with nextActions when no Animator is present.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject name, hierarchy path, or instance id with an Animator component." } }, ["target"]),
    destructiveHint: false,
    playModeSupport: "supported",
    equivalentUnityMcp: "manage_animation.animator_get_info",
    handler: "bridge",
    cliCommand: "tools run animator.get-info",
    docsTopic: "animation",
    examples: [
      { description: "Inspect Animator parameters and layers", argv: ["tools", "run", "animator.get-info", "--input", "{\"target\":\"UnityCliAlignmentProbe\"}"] }
    ]
  },
  {
    name: "animator.get-parameter",
    summary: "Get the current value of an Animator parameter by name.",
    description: "Get the current value of an Animator parameter by name. Read-only; returns parameter name, type (Float/Int/Bool/Trigger), and value. Returns E_ANIMATOR_PARAMETER_NOT_FOUND when the parameter name does not exist.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject name, hierarchy path, or instance id with an Animator component." }, parameterName: { type: "string", description: "Exact Animator parameter name." } }, ["target", "parameterName"]),
    destructiveHint: false,
    playModeSupport: "supported",
    equivalentUnityMcp: "manage_animation.animator_get_parameter",
    handler: "bridge",
    cliCommand: "tools run animator.get-parameter",
    docsTopic: "animation",
    examples: [
      { description: "Read an Animator parameter", argv: ["tools", "run", "animator.get-parameter", "--input", "{\"target\":\"UnityCliAlignmentProbe\",\"parameterName\":\"Speed\"}"] }
    ]
  },
  {
    name: "animator.set-parameter",
    summary: "Set an Animator parameter default on the assigned AnimatorController asset.",
    description: "Set an Animator parameter default on the assigned AnimatorController asset. Session-affecting: saves controller asset defaults; it does not drive the current runtime parameter value. Trigger parameters have no stored default and are acknowledged as a no-op. Returns E_ANIMATOR_PARAMETER_NOT_FOUND or E_ANIMATOR_NO_CONTROLLER when setup is incomplete.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject name, hierarchy path, or instance id with an Animator component." }, parameterName: { type: "string", description: "Exact AnimatorController parameter name." }, parameterType: { type: "string", enum: ["float", "int", "bool", "trigger"], description: "Optional expected parameter type; actual controller parameter type wins." }, value: { type: "string", description: "Default value to store on the AnimatorController asset." } }, ["target", "parameterName", "value"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.animator_set_parameter",
    handler: "bridge",
    cliCommand: "tools run animator.set-parameter",
    docsTopic: "animation",
    examples: [
      { description: "Set a Controller parameter default", argv: ["tools", "run", "animator.set-parameter", "--input", "{\"target\":\"UnityCliAlignmentProbe\",\"parameterName\":\"Speed\",\"parameterType\":\"float\",\"value\":\"1.5\"}"] }
    ]
  },
  {
    name: "animator.crossfade",
    summary: "Cross-fade the Animator to a named state over a given duration.",
    description: "Cross-fade the Animator to a named state over a given duration. Session-affecting: changes current Animator state; in Play Mode this affects only the running session, while edit-mode calls can mark the Animator dirty. Defaults: duration 0.25s, layer -1.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject name, hierarchy path, or instance id with an Animator component." }, state: { type: "string", description: "Animator state name or full path, e.g. Base Layer.Idle." }, duration: { type: "number", description: "Crossfade duration in seconds; defaults to 0.25." }, layer: { type: "integer", description: "Animator layer index, or -1 for Unity's default layer resolution." } }, ["target", "state"]),
    destructiveHint: false,
    sideEffects: ["animation-state", "scene-modify"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.animator_crossfade",
    handler: "bridge",
    cliCommand: "tools run animator.crossfade",
    docsTopic: "animation",
    examples: [
      { description: "Crossfade to a state", argv: ["tools", "run", "animator.crossfade", "--input", "{\"target\":\"UnityCliAlignmentProbe\",\"state\":\"Base Layer.Idle\",\"duration\":0.25,\"layer\":0}"] }
    ]
  },
  {
    name: "animator.set-speed",
    summary: "Set the Animator playback speed.",
    description: "Set the Animator playback speed. Session-affecting: writes the Animator component speed and may mark the scene dirty; speed 0 pauses playback, 1 restores normal speed. Returns E_ANIMATOR_NOT_FOUND when no Animator is present.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject name, hierarchy path, or instance id with an Animator component." }, speed: { type: "number", description: "Animator playback speed; defaults to 1.0 when omitted." } }, ["target"]),
    destructiveHint: false,
    sideEffects: ["animation-state", "scene-modify"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.animator_set_speed",
    handler: "bridge",
    cliCommand: "tools run animator.set-speed",
    docsTopic: "animation",
    examples: [
      { description: "Set Animator speed", argv: ["tools", "run", "animator.set-speed", "--input", "{\"target\":\"UnityCliAlignmentProbe\",\"speed\":1.25}"] }
    ]
  },
  {
    name: "animator.set-enabled",
    summary: "Enable or disable the Animator component.",
    description: "Enable or disable the Animator component. Session-affecting: toggles component state and may mark the scene dirty. Default is true (enable). Returns E_ANIMATOR_NOT_FOUND when no Animator is present.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject name, hierarchy path, or instance id with an Animator component." }, value: { type: "boolean", description: "Whether the Animator component should be enabled; defaults to true." } }, ["target"]),
    destructiveHint: false,
    sideEffects: ["animation-state", "scene-modify"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.animator_set_enabled",
    handler: "bridge",
    cliCommand: "tools run animator.set-enabled",
    docsTopic: "animation",
    examples: [
      { description: "Disable Animator", argv: ["tools", "run", "animator.set-enabled", "--input", "{\"target\":\"UnityCliAlignmentProbe\",\"value\":false}"] },
      { description: "Enable Animator", argv: ["tools", "run", "animator.set-enabled", "--input", "{\"target\":\"UnityCliAlignmentProbe\",\"value\":true}"] }
    ]
  },
  // ── controller.* AnimatorController editing tools ───
  {
    name: "controller.create",
    summary: "Create a new AnimatorController asset at an Assets/*.controller path.",
    description: "Create a new AnimatorController asset at an Assets/*.controller path. Session-affecting: writes an asset and refreshes AssetDatabase/import state. Returns E_CONTROLLER_PATH_REQUIRED, E_CONTROLLER_PATH_INVALID, or E_CONTROLLER_EXISTS with recovery guidance.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "Unity asset path ending in .controller, e.g. Assets/Animations/Player.controller." } }, ["controllerPath"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_create",
    handler: "bridge",
    cliCommand: "tools run controller.create",
    docsTopic: "animation",
    examples: [
      { description: "Create a controller", argv: ["tools", "run", "controller.create", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\"}"] }
    ]
  },
  {
    name: "controller.add-state",
    summary: "Add a state to an AnimatorController state machine.",
    description: "Add a state to an AnimatorController state machine. Optionally assign an AnimationClip, set playback speed, and mark it as default. Session-affecting: writes the controller asset. Returns E_CLIP_NOT_FOUND, E_STATE_EXISTS, or E_LAYER_INDEX_INVALID with recovery guidance.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, stateName: { type: "string", description: "New state name." }, clipPath: { type: "string", description: "Optional AnimationClip asset path to assign as state motion." }, layerIndex: { type: "integer", description: "Target layer index; defaults to 0." }, speed: { type: "number", description: "State playback speed; defaults to 1.0." }, isDefault: { type: "boolean", description: "Whether to set the state as the layer default state." } }, ["controllerPath", "stateName"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_add_state",
    handler: "bridge",
    cliCommand: "tools run controller.add-state",
    docsTopic: "animation",
    examples: [
      { description: "Add default Idle state", argv: ["tools", "run", "controller.add-state", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"stateName\":\"Idle\",\"isDefault\":true}"] },
      { description: "Add state with clip", argv: ["tools", "run", "controller.add-state", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"stateName\":\"Run\",\"clipPath\":\"Assets/UnityCliAlignmentFixtures/Run.anim\",\"speed\":1.0}"] }
    ]
  },
  {
    name: "controller.add-transition",
    summary: "Add a transition between two states in an AnimatorController.",
    description: "Add a transition between two states in an AnimatorController. Supports AnyState as source. Conditions can be a JSON array of {parameter, mode, threshold}. Session-affecting: writes the controller asset. Returns E_STATE_NOT_FOUND or E_LAYER_INDEX_INVALID on invalid states/layers.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, fromState: { type: "string", description: "Source state name, AnyState, Any, or Any State." }, toState: { type: "string", description: "Destination state name." }, layerIndex: { type: "integer", description: "Target layer index; defaults to 0." }, hasExitTime: { type: "boolean", description: "Whether the transition uses exit time; defaults to true." }, duration: { type: "number", description: "Transition duration; defaults to 0.25." }, exitTime: { type: "number", description: "Exit time; defaults to 0.75." }, conditions: { type: "array", description: "Optional transition conditions.", items: { type: "object", properties: { parameter: { type: "string" }, mode: { type: "string", enum: ["greater", "less", "equals", "notequal", "if", "ifnot"] }, threshold: { type: "number" } } } } }, ["controllerPath", "fromState", "toState"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_add_transition",
    handler: "bridge",
    cliCommand: "tools run controller.add-transition",
    docsTopic: "animation",
    examples: [
      { description: "Add transition", argv: ["tools", "run", "controller.add-transition", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"fromState\":\"Idle\",\"toState\":\"Run\",\"hasExitTime\":false,\"duration\":0.25}"] },
      { description: "Add conditional transition", argv: ["tools", "run", "controller.add-transition", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"fromState\":\"Idle\",\"toState\":\"Run\",\"conditions\":[{\"parameter\":\"Speed\",\"mode\":\"greater\",\"threshold\":0.1}]}"] }
    ]
  },
  {
    name: "controller.add-parameter",
    summary: "Add a parameter to an AnimatorController asset.",
    description: "Add a parameter to an AnimatorController asset. Supported types: float, int, bool, trigger. Session-affecting: writes controller asset defaults. Returns E_CONTROLLER_NOT_FOUND, E_PARAMETER_NAME_REQUIRED, or E_PARAMETER_EXISTS on duplicate.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, parameterName: { type: "string", description: "New parameter name." }, parameterType: { type: "string", enum: ["float", "int", "bool", "trigger"], description: "Animator parameter type; defaults to float." }, defaultValue: { type: "string", description: "Optional default value; ignored for trigger." } }, ["controllerPath", "parameterName"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_add_parameter",
    handler: "bridge",
    cliCommand: "tools run controller.add-parameter",
    docsTopic: "animation",
    examples: [
      { description: "Add float parameter", argv: ["tools", "run", "controller.add-parameter", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"parameterName\":\"Speed\",\"parameterType\":\"float\",\"defaultValue\":\"0\"}"] },
      { description: "Add trigger parameter", argv: ["tools", "run", "controller.add-parameter", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"parameterName\":\"Jump\",\"parameterType\":\"trigger\"}"] }
    ]
  },
  {
    name: "controller.get-info",
    summary: "Get detailed AnimatorController asset info including layers, states, BlendTree parameters/children, per-state transitions, AnyState transitions, transition...",
    description: "Get detailed AnimatorController asset info including layers, states, BlendTree parameters/children, per-state transitions, AnyState transitions, transition conditions, and parameters. Read-only; use before/after add-state/add-transition/assign/blend-tree edits to avoid missing names and audit state-machine edits. Returns E_CONTROLLER_NOT_FOUND or E_CONTROLLER_PATH_REQUIRED.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." } }, ["controllerPath"]),
    outputSchema: objSchema({ path: { type: "string" }, guid: { type: "string" }, name: { type: "string" }, layerCount: { type: "integer" }, parameterCount: { type: "integer" }, layers: { type: "array", items: { type: "object", properties: { index: { type: "integer" }, name: { type: "string" }, stateCount: { type: "integer" }, anyStateTransitionCount: { type: "integer" }, states: { type: "array", items: { type: "object", properties: { name: { type: "string" }, motionType: { type: "string" }, blendTree: { type: "object" }, transitions: { type: "array", items: { type: "object" } } } } }, anyStateTransitions: { type: "array", items: { type: "object" } } } } }, parameters: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    equivalentUnityMcp: "manage_animation.controller_get_info",
    handler: "bridge",
    cliCommand: "tools run controller.get-info",
    docsTopic: "animation",
    examples: [
      { description: "Inspect controller", argv: ["tools", "run", "controller.get-info", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\"}"] }
    ]
  },
  {
    name: "controller.assign",
    summary: "Assign an AnimatorController asset to a target GameObject's Animator component.",
    description: "Assign an AnimatorController asset to a target GameObject's Animator component. Adds an Animator component if one does not exist. Session-affecting: modifies scene/component state and may refresh Animator runtime caches. Returns E_CONTROLLER_NOT_FOUND or E_TARGET_NOT_FOUND.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, target: { type: "string", description: "GameObject name, hierarchy path, or instance id." } }, ["controllerPath", "target"]),
    destructiveHint: false,
    sideEffects: ["component-add", "component-modify", "scene-modify"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_assign",
    handler: "bridge",
    cliCommand: "tools run controller.assign",
    docsTopic: "animation",
    examples: [
      { description: "Assign controller to GameObject", argv: ["tools", "run", "controller.assign", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"target\":\"UnityCliAlignmentProbe\"}"] }
    ]
  },
  {
    name: "controller.add-layer",
    summary: "Add a layer to an AnimatorController.",
    description: "Add a layer to an AnimatorController. Set name, default weight, and blending mode (override or additive). Session-affecting: writes the controller asset. Returns E_CONTROLLER_NOT_FOUND or E_LAYER_NAME_REQUIRED on invalid input.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, layerName: { type: "string", description: "New layer name." }, weight: { type: "number", description: "Default layer weight; defaults to 1.0." }, blendingMode: { type: "string", enum: ["override", "additive"], description: "Layer blending mode; defaults to override." } }, ["controllerPath", "layerName"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_add_layer",
    handler: "bridge",
    cliCommand: "tools run controller.add-layer",
    docsTopic: "animation",
    examples: [
      { description: "Add additive layer", argv: ["tools", "run", "controller.add-layer", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"layerName\":\"UpperBody\",\"weight\":0.8,\"blendingMode\":\"additive\"}"] }
    ]
  },
  {
    name: "controller.remove-layer",
    summary: "Remove a non-base layer from an AnimatorController by index or name.",
    description: "Remove a non-base layer from an AnimatorController by index or name. Destructive: deletes a controller layer and its states/transitions. Requires either layerIndex or layerName. Returns E_LAYER_BASE_IMMUTABLE, E_LAYER_NOT_FOUND, or E_LAYER_IDENTIFIER_REQUIRED.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, layerIndex: { type: "integer", description: "Layer index to remove; cannot be 0." }, layerName: { type: "string", description: "Layer name to remove." } }, ["controllerPath"]),
    destructiveHint: true,
    supportsDryRun: false,
    sideEffects: ["animator-controller-modify", "asset-write", "asset-delete-nested"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_remove_layer",
    handler: "bridge",
    cliCommand: "tools run controller.remove-layer",
    docsTopic: "animation",
    examples: [
      { description: "Remove layer by name", argv: ["tools", "run", "controller.remove-layer", "--yes", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"layerName\":\"UpperBody\"}"] }
    ]
  },
  {
    name: "controller.set-layer-weight",
    summary: "Set the default weight of an AnimatorController layer by index or name.",
    description: "Set the default weight of an AnimatorController layer by index or name. Session-affecting: writes the controller asset. Requires either layerIndex or layerName with weight. Returns E_LAYER_IDENTIFIER_REQUIRED or E_LAYER_NOT_FOUND when the layer cannot be resolved.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, layerIndex: { type: "integer", description: "Layer index to update." }, layerName: { type: "string", description: "Layer name to update." }, weight: { type: "number", description: "New default layer weight." } }, ["controllerPath", "weight"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_set_layer_weight",
    handler: "bridge",
    cliCommand: "tools run controller.set-layer-weight",
    docsTopic: "animation",
    examples: [
      { description: "Set layer weight", argv: ["tools", "run", "controller.set-layer-weight", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"layerName\":\"UpperBody\",\"weight\":0.5}"] }
    ]
  },
  {
    name: "controller.create-blend-tree-1d",
    summary: "Create a 1D BlendTree as a new AnimatorController state.",
    description: "Create a 1D BlendTree as a new AnimatorController state. Session-affecting: writes a nested BlendTree asset inside the controller. Requires stateName and blendParameter. Returns E_STATE_EXISTS, E_LAYER_INDEX_INVALID, or E_BLEND_PARAMETER_REQUIRED on invalid input.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, stateName: { type: "string", description: "New state name that will hold the BlendTree motion." }, blendParameter: { type: "string", description: "Blend parameter for Simple1D." }, layerIndex: { type: "integer", description: "Target layer index; defaults to 0." } }, ["controllerPath", "stateName", "blendParameter"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write", "asset-add-nested"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_create_blend_tree_1d",
    handler: "bridge",
    cliCommand: "tools run controller.create-blend-tree-1d",
    docsTopic: "animation",
    examples: [
      { description: "Create 1D blend tree", argv: ["tools", "run", "controller.create-blend-tree-1d", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"stateName\":\"Locomotion\",\"blendParameter\":\"Speed\"}"] }
    ]
  },
  {
    name: "controller.create-blend-tree-2d",
    summary: "Create a 2D BlendTree as a new AnimatorController state.",
    description: "Create a 2D BlendTree as a new AnimatorController state. Session-affecting: writes a nested BlendTree asset inside the controller. Supported blend types: simpledirectional2d, freeformdirectional2d, freeformcartesian2d. Requires stateName, blendParameterX, and blendParameterY.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, stateName: { type: "string", description: "New state name that will hold the BlendTree motion." }, blendParameterX: { type: "string", description: "X-axis blend parameter." }, blendParameterY: { type: "string", description: "Y-axis blend parameter." }, layerIndex: { type: "integer", description: "Target layer index; defaults to 0." }, blendType: { type: "string", enum: ["simpledirectional2d", "freeformdirectional2d", "freeformcartesian2d"], description: "BlendTreeType; defaults to simpledirectional2d." } }, ["controllerPath", "stateName", "blendParameterX", "blendParameterY"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write", "asset-add-nested"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_create_blend_tree_2d",
    handler: "bridge",
    cliCommand: "tools run controller.create-blend-tree-2d",
    docsTopic: "animation",
    examples: [
      { description: "Create 2D blend tree", argv: ["tools", "run", "controller.create-blend-tree-2d", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"stateName\":\"Movement\",\"blendParameterX\":\"VelocityX\",\"blendParameterY\":\"VelocityY\",\"blendType\":\"freeformcartesian2d\"}"] }
    ]
  },
  {
    name: "controller.add-blend-tree-child",
    summary: "Add a child AnimationClip to an existing BlendTree state.",
    description: "Add a child AnimationClip to an existing BlendTree state. For 1D BlendTrees, pass threshold. For 2D BlendTrees, pass positionX and positionY (CoplayDev uses a position tuple; this CLI keeps explicit fields). Session-affecting: modifies the nested BlendTree asset.",
    group: "controller",
    inputSchema: objSchema({ controllerPath: { type: "string", description: "AnimatorController asset path." }, stateName: { type: "string", description: "Existing state whose motion is a BlendTree." }, clipPath: { type: "string", description: "AnimationClip asset path to add as a child motion." }, layerIndex: { type: "integer", description: "Target layer index; defaults to 0." }, threshold: { type: "number", description: "Required for Simple1D BlendTrees." }, positionX: { type: "number", description: "Required for 2D BlendTrees." }, positionY: { type: "number", description: "Required for 2D BlendTrees." } }, ["controllerPath", "stateName", "clipPath"]),
    destructiveHint: false,
    sideEffects: ["animator-controller-modify", "asset-write", "asset-add-nested"],
    playModeSupport: "session-affecting",
    equivalentUnityMcp: "manage_animation.controller_add_blend_tree_child",
    handler: "bridge",
    cliCommand: "tools run controller.add-blend-tree-child",
    docsTopic: "animation",
    examples: [
      { description: "Add 1D child clip", argv: ["tools", "run", "controller.add-blend-tree-child", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"stateName\":\"Locomotion\",\"clipPath\":\"Assets/UnityCliAlignmentFixtures/Walk.anim\",\"threshold\":1}"] },
      { description: "Add 2D child clip", argv: ["tools", "run", "controller.add-blend-tree-child", "--input", "{\"controllerPath\":\"Assets/UnityCliAlignmentFixtures/NewAnimator.controller\",\"stateName\":\"Movement\",\"clipPath\":\"Assets/UnityCliAlignmentFixtures/WalkForward.anim\",\"positionX\":0,\"positionY\":1}"] }
    ]
  }
];
