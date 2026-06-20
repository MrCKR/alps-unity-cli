import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const ANIMATION_TOOLS: ToolMetadata[] = [
  {
    name: "animation.clip",
    title: "Animation Clip",
    description: "Get info on an animation clip, optionally create one.",
    group: "animation",
    inputSchema: objSchema({ path: { type: "string" }, action: { type: "string", enum: ["info", "create"] }, create: { type: "boolean" }, overwrite: { type: "boolean" }, frameRate: { type: "number" }, legacy: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animation clip",
    docsTopic: "animation"
  },
  {
    name: "animation.state",
    title: "Animation State",
    description: "Query the current animation state on a target.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" }, layer: { type: "integer" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animation state",
    docsTopic: "animation"
  },
  {
    name: "animation.play",
    title: "Play Animation",
    description: "Play an animation state on a target GameObject.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" }, state: { type: "string" }, layer: { type: "integer" }, normalizedTime: { type: "number" } }, ["target", "state"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animation play",
    docsTopic: "animation"
  },
  {
    name: "animation.stop",
    title: "Stop Animation",
    description: "Stop the currently playing animation on a target.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animation stop",
    docsTopic: "animation"
  },
  {
    name: "animator.get-info",
    title: "Animator Get Info",
    description: "Get comprehensive Animator component info including enabled state, speed, controller, parameters, layers, and clips. Returns E_ANIMATOR_NOT_FOUND with nextActions when no Animator is present.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animator get-info",
    docsTopic: "animation"
  },
  {
    name: "animator.get-parameter",
    title: "Animator Get Parameter",
    description: "Get the current value of an Animator parameter by name. Returns parameter name, type (Float/Int/Bool/Trigger), and value. Returns E_ANIMATOR_PARAMETER_NOT_FOUND when the parameter name does not exist.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string" }, parameterName: { type: "string" } }, ["target", "parameterName"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animator get-parameter",
    docsTopic: "animation"
  },
  {
    name: "animator.set-parameter",
    title: "Animator Set Parameter",
    description: "Set the default value of an Animator parameter. In edit mode, modifies the AnimatorController asset defaults (runtime Set methods are no-ops outside Play Mode). Triggers are runtime-only and produce a no-op in edit mode. Returns E_ANIMATOR_PARAMETER_NOT_FOUND when the parameter name does not exist, E_ANIMATOR_NO_CONTROLLER when no AnimatorController is assigned.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string" }, parameterName: { type: "string" }, parameterType: { type: "string" }, value: { type: "string" } }, ["target", "parameterName", "value"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animator set-parameter",
    docsTopic: "animation"
  },
  {
    name: "animator.crossfade",
    title: "Animator Crossfade",
    description: "Cross-fade the Animator to a named state over a given duration. Defaults: duration 0.25s, layer -1 (first layer with the state). Returns E_ANIMATION_STATE_REQUIRED when state is missing, E_ANIMATION_NO_CONTROLLER when no controller is assigned.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string" }, state: { type: "string" }, duration: { type: "number" }, layer: { type: "integer" } }, ["target", "state"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animator crossfade",
    docsTopic: "animation"
  },
  {
    name: "animator.set-speed",
    title: "Animator Set Speed",
    description: "Set the Animator playback speed. Default speed is 1.0. Returns E_ANIMATOR_NOT_FOUND when no Animator is present.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string" }, speed: { type: "number" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animator set-speed",
    docsTopic: "animation"
  },
  {
    name: "animator.set-enabled",
    title: "Animator Set Enabled",
    description: "Enable or disable the Animator component. Default is true (enable). Returns E_ANIMATOR_NOT_FOUND when no Animator is present.",
    group: "animator",
    inputSchema: objSchema({ target: { type: "string" }, value: { type: "boolean" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animator set-enabled",
    docsTopic: "animation"
  }
];
