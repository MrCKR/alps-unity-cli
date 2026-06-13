import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import type { CliResult } from "./types.js";
import { failure, success } from "./output.js";

export interface BridgeRuntime {
  bridgeEndpoint?: string;
  bridgeExecute?: (payload: BridgeRequest) => Promise<BridgeResponse>;
}

export interface BridgeRequest {
  requestId: string;
  command: string;
  args: Record<string, unknown>;
}

interface BridgeResponse {
  ok?: boolean;
  data?: Record<string, unknown>;
  error?: {
    code?: string;
    message?: string;
    details?: unknown;
  };
  nextActions?: string[];
}

export async function executeBridgeCommand(
  requestId: string,
  command: string,
  flags: Record<string, string | boolean>,
  positionalTarget: string | undefined,
  runtime: BridgeRuntime
): Promise<CliResult> {
  return executeBridgePayload(requestId, command, await commandArgs(command, flags, positionalTarget), flags, runtime);
}

export async function executeBridgePayload(
  requestId: string,
  command: string,
  args: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime
): Promise<CliResult> {
  const endpoint = bridgeEndpoint(flags, runtime);
  if (!runtime.bridgeExecute && !endpoint) {
    return failure(requestId, "BRIDGE_ENDPOINT_REQUIRED", "Pass --bridge-endpoint <url> or set ALPS_UNITY_BRIDGE_ENDPOINT for live query/edit/screenshot/code commands.", [
      "Open Unity with the Alps Unity Bridge package installed.",
      "Pass --bridge-endpoint http://127.0.0.1:<port>."
    ]);
  }

  try {
    const bridgeResult = runtime.bridgeExecute
      ? await runtime.bridgeExecute({ requestId, command, args })
      : await postBridgeExecute(endpoint!, { requestId, command, args });

    if (bridgeResult.ok === false) {
      return failure(requestId, bridgeResult.error?.code ?? "BRIDGE_COMMAND_FAILED", bridgeResult.error?.message ?? "Unity Bridge rejected the command.", bridgeResult.nextActions ?? [], bridgeResult.error?.details);
    }

    if (command === "code.exec" && bridgeResult.data?.status === "running" && bridgeResult.data?.sideEffectSemantics === "started-before-http-timeout") {
      return liveCodeTimeoutFailure(requestId, endpoint, args, bridgeResult.data);
    }

    return success(requestId, {
      command,
      bridgeEndpoint: endpoint,
      ...bridgeResult.data
    });
  } catch (error) {
    if (command === "code.exec" && isAbortLikeError(error)) {
      return liveCodeTimeoutFailure(requestId, endpoint, args, { cause: errorMessage(error) });
    }
    return failure(requestId, "BRIDGE_CONNECTION_FAILED", errorMessage(error), ["Check Unity Bridge endpoint and logs."]);
  }
}

export function bridgeCommandName(group: string | undefined, command: string | undefined): string | undefined {
  if (group === "scene" && ["active", "hierarchy", "build-settings", "screenshot", "create", "load", "save"].includes(command ?? "")) {
    return `scene.${command}`;
  }
  if (group === "camera" && ["list", "info", "screenshot"].includes(command ?? "")) {
    return `camera.${command}`;
  }
  if (group === "go" && ["find", "inspect", "create", "modify", "delete"].includes(command ?? "")) {
    return `go.${command}`;
  }
  if (group === "component" && ["add", "remove", "set"].includes(command ?? "")) {
    return `component.${command}`;
  }
  if (group === "asset" && ["search", "info", "mkdir", "import", "move", "delete"].includes(command ?? "")) {
    return `asset.${command}`;
  }
  if (group === "prefab" && ["info", "create", "stage-open", "stage-save", "stage-close", "hierarchy"].includes(command ?? "")) {
    return `prefab.${command}`;
  }
  if (group === "material" && ["info", "create", "assign", "set"].includes(command ?? "")) {
    return `material.${command}`;
  }
  if (group === "graphics" && ["quality", "settings", "screenshot"].includes(command ?? "")) {
    return `graphics.${command}`;
  }
  if (group === "physics" && ["settings", "raycast", "simulation"].includes(command ?? "")) {
    return `physics.${command}`;
  }
  if (group === "script" && ["create", "read", "edit", "validate"].includes(command ?? "")) {
    return `script.${command}`;
  }
  if ((group === "preflight" || group === "diagnose") && (!command || command === "run")) {
    return "diagnose.preflight";
  }
  if (group === "editor" && command === "console") {
    return "editor.console";
  }
  if (group === "screenshot" && command === "capture") {
    return "screenshot.capture";
  }
  if (group === "code" && command === "exec") {
    return "code.exec";
  }
  return undefined;
}

async function commandArgs(command: string, flags: Record<string, string | boolean>, positionalTarget: string | undefined): Promise<Record<string, unknown>> {
  if (command.startsWith("scene.")) {
    if (["scene.create", "scene.load", "scene.save"].includes(command)) {
      return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path"), name: stringFlag(flags, "name") });
    }
    if (command === "scene.screenshot") {
      return screenshotArgs(flags, positionalTarget);
    }
    return queryArgs(flags);
  }

  if (command.startsWith("camera.")) {
    if (command === "camera.list") {
      return queryArgs(flags);
    }
    if (command === "camera.info") {
      return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") });
    }
    return compactArgs({ ...screenshotArgs(flags, undefined), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") });
  }

  if (command === "go.find" || command === "go.inspect") {
    return targetArgs(flags, positionalTarget);
  }

  if (command.startsWith("go.")) {
    return compactArgs({
      ...targetArgs(flags, positionalTarget),
      newName: stringFlag(flags, "new-name") ?? stringFlag(flags, "set-name"),
      parent: stringFlag(flags, "parent"),
      active: boolFlag(flags, "active"),
      layerValue: stringFlag(flags, "layer-value") ?? stringFlag(flags, "set-layer"),
      tagValue: stringFlag(flags, "tag-value") ?? stringFlag(flags, "set-tag"),
      primitive: stringFlag(flags, "primitive"),
      x: stringFlag(flags, "x"),
      y: stringFlag(flags, "y"),
      z: stringFlag(flags, "z"),
      rotationX: stringFlag(flags, "rotation-x"),
      rotationY: stringFlag(flags, "rotation-y"),
      rotationZ: stringFlag(flags, "rotation-z"),
      scaleX: stringFlag(flags, "scale-x"),
      scaleY: stringFlag(flags, "scale-y"),
      scaleZ: stringFlag(flags, "scale-z")
    });
  }

  if (command.startsWith("component.")) {
    return compactArgs({
      ...targetArgs(flags, positionalTarget),
      component: stringFlag(flags, "component") ?? stringFlag(flags, "type"),
      property: stringFlag(flags, "property"),
      value: stringFlag(flags, "value")
    });
  }

  if (command === "asset.search" || command === "asset.info") {
    return compactArgs({
      ...queryArgs(flags),
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "query"),
      query: stringFlag(flags, "query") ?? stringFlag(flags, "filter") ?? positionalTarget,
      type: stringFlag(flags, "type"),
      path: stringFlag(flags, "path"),
      guid: stringFlag(flags, "guid")
    });
  }

  if (command.startsWith("asset.")) {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "source"),
      source: stringFlag(flags, "source"),
      destination: stringFlag(flags, "destination") ?? stringFlag(flags, "to"),
      type: stringFlag(flags, "type")
    });
  }

  if (command.startsWith("prefab.")) {
    if (command === "prefab.create") {
      return compactArgs({
        source: positionalTarget ?? stringFlag(flags, "source") ?? stringFlag(flags, "target"),
        path: stringFlag(flags, "path") ?? stringFlag(flags, "destination") ?? stringFlag(flags, "to"),
        overwrite: boolFlag(flags, "overwrite")
      });
    }
    if (command === "prefab.stage-save" || command === "prefab.stage-close") {
      return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path"), save: boolFlag(flags, "save") });
    }
    return compactArgs({
      ...queryArgs(flags),
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "prefab")
    });
  }

  if (command.startsWith("material.")) {
    if (command === "material.create") {
      return compactArgs({
        path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "material"),
        shader: stringFlag(flags, "shader"),
        overwrite: boolFlag(flags, "overwrite")
      });
    }
    if (command === "material.assign") {
      return compactArgs({
        target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
        material: stringFlag(flags, "material") ?? stringFlag(flags, "path"),
        slot: numberFlag(flags, "slot") ?? numberFlag(flags, "material-index")
      });
    }
    if (command === "material.set") {
      return compactArgs({
        path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "material"),
        property: stringFlag(flags, "property"),
        value: stringFlag(flags, "value"),
        type: stringFlag(flags, "type")
      });
    }
    return compactArgs({
      ...queryArgs(flags),
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "material")
    });
  }

  if (command.startsWith("graphics.")) {
    if (command === "graphics.screenshot") {
      return compactArgs({ ...screenshotArgs(flags, positionalTarget), includeDiagnostics: boolFlag(flags, "include-diagnostics") });
    }
    return compactArgs({
      ...queryArgs(flags),
      property: stringFlag(flags, "property"),
      value: stringFlag(flags, "value"),
      qualityLevel: numberFlag(flags, "quality-level") ?? numberFlag(flags, "level"),
      vSyncCount: numberFlag(flags, "v-sync-count"),
      antiAliasing: numberFlag(flags, "anti-aliasing"),
      shadowDistance: floatFlag(flags, "shadow-distance"),
      masterTextureLimit: numberFlag(flags, "master-texture-limit"),
      lodBias: floatFlag(flags, "lod-bias"),
      maximumLODLevel: numberFlag(flags, "maximum-lod-level"),
      anisotropicFiltering: stringFlag(flags, "anisotropic-filtering"),
      shadows: stringFlag(flags, "shadows"),
      shadowResolution: stringFlag(flags, "shadow-resolution"),
      realtimeReflectionProbes: boolFlag(flags, "realtime-reflection-probes"),
      softParticles: boolFlag(flags, "soft-particles"),
      renderPipeline: stringFlag(flags, "render-pipeline")
    });
  }

  if (command.startsWith("physics.")) {
    if (command === "physics.raycast") {
      return compactArgs({
        mode: stringFlag(flags, "mode"),
        originX: floatFlag(flags, "origin-x") ?? floatFlag(flags, "x"),
        originY: floatFlag(flags, "origin-y") ?? floatFlag(flags, "y"),
        originZ: floatFlag(flags, "origin-z") ?? floatFlag(flags, "z"),
        directionX: floatFlag(flags, "direction-x") ?? floatFlag(flags, "dx"),
        directionY: floatFlag(flags, "direction-y") ?? floatFlag(flags, "dy"),
        directionZ: floatFlag(flags, "direction-z") ?? floatFlag(flags, "dz"),
        distance: floatFlag(flags, "distance"),
        layerMask: numberFlag(flags, "layer-mask"),
        all: boolFlag(flags, "all")
      });
    }
    if (command === "physics.simulation") {
      return compactArgs({ mode: stringFlag(flags, "mode"), steps: numberFlag(flags, "steps"), deltaTime: floatFlag(flags, "delta-time"), simulate: boolFlag(flags, "simulate") });
    }
    return compactArgs({
      mode: stringFlag(flags, "mode"),
      property: stringFlag(flags, "property"),
      value: stringFlag(flags, "value"),
      gravityX: floatFlag(flags, "gravity-x"),
      gravityY: floatFlag(flags, "gravity-y"),
      gravityZ: floatFlag(flags, "gravity-z"),
      gravity2dX: floatFlag(flags, "gravity2d-x"),
      gravity2dY: floatFlag(flags, "gravity2d-y"),
      defaultContactOffset: floatFlag(flags, "default-contact-offset"),
      bounceThreshold: floatFlag(flags, "bounce-threshold"),
      defaultSolverIterations: numberFlag(flags, "default-solver-iterations"),
      defaultSolverVelocityIterations: numberFlag(flags, "default-solver-velocity-iterations"),
      queriesHitTriggers: boolFlag(flags, "queries-hit-triggers"),
      queriesHitBackfaces: boolFlag(flags, "queries-hit-backfaces"),
      queriesStartInColliders: boolFlag(flags, "queries-start-in-colliders"),
      autoSimulation: boolFlag(flags, "auto-simulation")
    });
  }

  if (command.startsWith("script.")) {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path"),
      name: stringFlag(flags, "name"),
      content: await contentFlag(flags),
      patch: stringFlag(flags, "patch"),
      find: stringFlag(flags, "find"),
      replace: stringFlag(flags, "replace"),
      all: boolFlag(flags, "all")
    });
  }

  if (command === "diagnose.preflight") {
    return compactArgs({ ...targetArgs(flags, positionalTarget), path: stringFlag(flags, "path") ?? positionalTarget, command: stringFlag(flags, "command") });
  }

  if (command === "editor.console") {
    return compactArgs({ ...queryArgs(flags), type: stringFlag(flags, "type"), count: numberFlag(flags, "count") ?? numberFlag(flags, "limit"), clear: boolFlag(flags, "clear") });
  }

  if (command === "screenshot.capture") {
    return screenshotArgs(flags, positionalTarget);
  }

  if (command === "code.exec") {
    return compactArgs({
      sourcePath: stringFlag(flags, "file"),
      sourceText: stringFlag(flags, "code"),
      sourceKind: stringFlag(flags, "file") ? "file" : stringFlag(flags, "code") ? "inline" : boolFlag(flags, "stdin") ? "stdin" : undefined,
      channel: stringFlag(flags, "channel"),
      timeoutMs: numberFlag(flags, "timeout-ms"),
      artifactDir: stringFlag(flags, "artifact-dir"),
      argsJson: stringFlag(flags, "args"),
      refs: stringFlag(flags, "refs"),
      defines: stringFlag(flags, "define")
    });
  }

  return {};
}

function screenshotArgs(flags: Record<string, string | boolean>, positionalPath: string | undefined): Record<string, unknown> {
  return compactArgs({
    path: positionalPath ?? stringFlag(flags, "path") ?? stringFlag(flags, "output"),
    format: stringFlag(flags, "format"),
    width: numberFlag(flags, "width"),
    height: numberFlag(flags, "height"),
    transparent: boolFlag(flags, "transparent"),
    quality: numberFlag(flags, "quality"),
    overwrite: boolFlag(flags, "overwrite"),
    gameView: boolFlag(flags, "game-view")
  });
}

function bridgeEndpoint(flags: Record<string, string | boolean>, runtime: BridgeRuntime): string | undefined {
  return stringFlag(flags, "bridge-endpoint") ?? runtime.bridgeEndpoint ?? process.env.ALPS_UNITY_BRIDGE_ENDPOINT;
}

async function postBridgeExecute(endpoint: string, payload: BridgeRequest): Promise<BridgeResponse> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), bridgeHttpTimeoutMs(payload));
  try {
    const response = await fetch(new URL("/bridge/execute", endpoint), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(payload),
      signal: controller.signal
    });
    if (!response.ok) {
      throw new Error(`Bridge returned HTTP ${response.status}.`);
    }
    return (await response.json()) as BridgeResponse;
  } finally {
    clearTimeout(timeout);
  }
}

function bridgeHttpTimeoutMs(payload: BridgeRequest): number {
  if (payload.command === "code.exec" && typeof payload.args.timeoutMs === "number" && Number.isFinite(payload.args.timeoutMs) && payload.args.timeoutMs > 0) {
    return payload.args.timeoutMs + 5000;
  }
  return 10_000;
}

function liveCodeTimeoutFailure(requestId: string, endpoint: string | undefined, args: Record<string, unknown>, details: Record<string, unknown>): CliResult {
  const timeoutMs = typeof args.timeoutMs === "number" && Number.isFinite(args.timeoutMs) ? args.timeoutMs : null;
  const logPath = typeof details.logPath === "string" ? details.logPath : "Unity project Logs/AlpsUnityBridge.log";
  return failure(requestId, "E_EXECUTION_TIMEOUT", timeoutMs === null ? "Runtime Code live execution did not complete before the bridge HTTP timeout." : `Runtime Code live execution did not complete within ${timeoutMs}ms.`, [
    "Inspect the Unity Editor for a long-running script or busy dialog.",
    "Inspect the Unity project Logs/AlpsUnityBridge.log for bridge_request_timeout_started and requestId.",
    "Retry after Unity is responsive, or increase --timeout-ms for intentionally long-running code."
  ], {
    requestId,
    command: "code.exec",
    channel: "live",
    timeoutMs,
    bridgeEndpoint: endpoint ?? null,
    audit: { requestId, command: "code.exec", channel: "live", timeoutMs },
    logs: { bridge: logPath },
    ...details
  });
}

function isAbortLikeError(error: unknown): boolean {
  const name = error instanceof Error ? error.name : "";
  const message = errorMessage(error);
  return name === "AbortError" || /\b(abort|aborted|timed? out|timeout)\b/i.test(message);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function queryArgs(flags: Record<string, string | boolean>): Record<string, unknown> {
  return compactArgs({ pageSize: numberFlag(flags, "page-size") ?? numberFlag(flags, "limit"), cursor: stringFlag(flags, "cursor"), filter: stringFlag(flags, "filter") });
}

function targetArgs(flags: Record<string, string | boolean>, positionalTarget: string | undefined): Record<string, unknown> {
  return compactArgs({
    ...queryArgs(flags),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    name: stringFlag(flags, "name"),
    tag: stringFlag(flags, "tag"),
    layer: stringFlag(flags, "layer"),
    component: stringFlag(flags, "component"),
    path: stringFlag(flags, "path"),
    id: stringFlag(flags, "id")
  });
}

function stringFlag(flags: Record<string, string | boolean>, key: string): string | undefined {
  return typeof flags[key] === "string" ? flags[key] : undefined;
}

function boolFlag(flags: Record<string, string | boolean>, key: string): boolean | string | undefined {
  if (flags[key] === true || flags[key] === "true") {
    return true;
  }
  if (flags[key] === "false") {
    return false;
  }
  return undefined;
}

async function contentFlag(flags: Record<string, string | boolean>): Promise<string | undefined> {
  const inline = stringFlag(flags, "content");
  if (inline !== undefined) {
    return inline;
  }
  const filePath = stringFlag(flags, "content-file");
  return filePath ? readFile(resolve(filePath), "utf8") : undefined;
}

function numberFlag(flags: Record<string, string | boolean>, key: string): number | undefined {
  const value = stringFlag(flags, key);
  if (!value) {
    return undefined;
  }
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function floatFlag(flags: Record<string, string | boolean>, key: string): number | undefined {
  const value = stringFlag(flags, key);
  if (!value) {
    return undefined;
  }
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function compactArgs(args: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(args).filter(([, value]) => value !== undefined));
}
