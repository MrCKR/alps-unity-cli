import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const GRAPHICS_TOOLS: ToolMetadata[] = [
  {
    name: "graphics.quality",
    title: "Graphics Quality Level",
    description: "Get or set the current quality level and VSync count.",
    group: "graphics",
    inputSchema: objSchema({
      qualityLevel: { type: "integer" },
      vSyncCount: { type: "integer" },
      antiAliasing: { type: "integer" },
      shadowDistance: { type: "number" },
      masterTextureLimit: { type: "integer" },
      lodBias: { type: "number" },
      maximumLODLevel: { type: "integer" },
      anisotropicFiltering: { type: "string" },
      shadows: { type: "string" },
      shadowResolution: { type: "string" },
      realtimeReflectionProbes: { type: "boolean" },
      softParticles: { type: "boolean" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics quality",
    docsTopic: "graphics"
  },
  {
    name: "graphics.settings",
    title: "Graphics Settings",
    description: "Get or set anti-aliasing and shadow distance.",
    group: "graphics",
    inputSchema: objSchema({
      antiAliasing: { type: "integer" },
      shadowDistance: { type: "number" },
      vSyncCount: { type: "integer" },
      masterTextureLimit: { type: "integer" },
      lodBias: { type: "number" },
      maximumLODLevel: { type: "integer" },
      renderPipeline: { type: "string" },
      property: { type: "string" },
      value: { type: "string" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics settings",
    docsTopic: "graphics"
  },
  {
    name: "graphics.pipeline",
    title: "Graphics Render Pipeline",
    description: "Query the active and default render pipeline summary.",
    group: "graphics",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics pipeline",
    docsTopic: "graphics"
  },
  {
    name: "graphics.volume",
    title: "Graphics Volume",
    description: "Query Volume components and assigned profiles in the active scene.",
    group: "graphics",
    inputSchema: objSchema({ target: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics volume",
    docsTopic: "graphics"
  },
  {
    name: "graphics.screenshot",
    title: "Graphics Screenshot",
    description: "Capture a screenshot with optional GPU diagnostics overlay.",
    group: "graphics",
    inputSchema: objSchema({ path: { type: "string" }, includeDiagnostics: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics screenshot",
    docsTopic: "graphics"
  }
];
