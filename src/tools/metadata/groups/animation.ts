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
  }
];
