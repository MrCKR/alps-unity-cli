import { readFile, stat } from "node:fs/promises";
import { resolve } from "node:path";
import { stdin } from "node:process";
import type { CliResult } from "./types.js";
import { durationMsFrom, failure, success, TRANSIENT_ERROR_CODES, DEFAULT_RETRY_AFTER_MS } from "./output.js";
import { withRetry, type RetryOptions } from "./envelope.js";
import { instanceSelectionFailure, selectInstanceForFlags, type InstanceSelection } from "./instances.js";

export interface BridgeRuntime {
  bridgeEndpoint?: string;
  dataDir?: string;
  bridgeExecute?: (payload: BridgeRequest) => Promise<BridgeResponse>;
}

export interface BridgeRequest {
  requestId: string;
  command: string;
  args: Record<string, unknown>;
}

class BridgeArgumentError extends Error {
  public constructor(public readonly code: string, message: string, public readonly nextActions: string[] = []) {
    super(message);
  }
}

interface BridgeError {
  code?: string;
  message?: string;
  [key: string]: unknown;
}

interface BridgeResponse {
  ok?: boolean;
  data?: Record<string, unknown>;
  error?: BridgeError;
  nextActions?: string[];
}

export async function executeBridgeCommand(
  requestId: string,
  command: string,
  flags: Record<string, string | boolean>,
  positionalTarget: string | undefined,
  runtime: BridgeRuntime
): Promise<CliResult> {
  try {
    return executeBridgePayload(requestId, command, await commandArgs(command, flags, positionalTarget), flags, runtime);
  } catch (error) {
    if (error instanceof BridgeArgumentError) {
      return failure(requestId, error.code, error.message, error.nextActions);
    }
    throw error;
  }
}

export async function executeBridgePayload(
  requestId: string,
  command: string,
  args: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime
): Promise<CliResult> {
  const startMs = Date.now();
  let selectedInstance: InstanceSelection | undefined;
  let endpoint: string | undefined;
  try {
    selectedInstance = command.startsWith("custom-tool.") ? undefined : await selectInstanceForFlags(flags, runtime.dataDir ?? process.cwd());
    endpoint = bridgeEndpoint(flags, runtime, selectedInstance);
  } catch (error) {
    const result = instanceSelectionFailure(requestId, error);
    if (result) {
      return result;
    }
    throw error;
  }
  if (!runtime.bridgeExecute && !endpoint) {
    if (command === "ui.document.info" && typeof args.path === "string") {
      return readUiDocumentOffline(requestId, command, args.path);
    }
    return failure(requestId, "BRIDGE_ENDPOINT_REQUIRED", "Pass --bridge-endpoint <url> or set ALPS_UNITY_BRIDGE_ENDPOINT for live query/edit/screenshot/code commands.", [
      "Open Unity with the Alps Unity Bridge package installed.",
      "Pass --bridge-endpoint http://127.0.0.1:<port>."
    ]);
  }

  // Check if retry is enabled via --retry flag
  const maxRetries = typeof flags["retry"] === "string" ? Number(flags["retry"]) : flags["retry"] === true ? 3 : 0;
  const retryOptions: RetryOptions = { maxRetries, onRetry: (attempt, error, retryAfterMs) => {
    process.stderr.write(`[retry] attempt ${attempt}/${maxRetries} after ${retryAfterMs}ms: ${error.error?.code} – ${error.error?.message}\n`);
  } };

  const result = await withRetry(async () => {
    try {
      const bridgeResult = runtime.bridgeExecute
        ? await runtime.bridgeExecute({ requestId, command, args })
        : await postBridgeExecute(endpoint!, { requestId, command, args });

      if (bridgeResult.ok === false) {
        const code = bridgeResult.error?.code ?? "BRIDGE_COMMAND_FAILED";
        const details = command === "code.exec" && code === "E_UNITY_BUSY" ? liveCodeBusyDetails(requestId, endpoint, args, bridgeErrorDetails(bridgeResult.error)) : bridgeErrorDetails(bridgeResult.error);
        return failure(requestId, code, bridgeResult.error?.message ?? "Unity Bridge rejected the command.", bridgeResult.nextActions ?? [], details);
      }

      if (command === "code.exec" && bridgeResult.data?.status === "running" && bridgeResult.data?.sideEffectSemantics === "started-before-http-timeout") {
        return liveCodeTimeoutFailure(requestId, endpoint, args, bridgeResult.data);
      }

      return success(requestId, {
        command,
        bridgeEndpoint: endpoint,
        ...(selectedInstance ? { selectedInstance: selectedInstanceSummary(selectedInstance) } : {}),
        ...bridgeResult.data
      });
    } catch (error) {
      if (command === "code.exec" && isAbortLikeError(error)) {
        return liveCodeTimeoutFailure(requestId, endpoint, args, { cause: errorMessage(error) });
      }
      if (command === "custom-tool.run" && isAbortLikeError(error)) {
        return failure(requestId, "E_EXECUTION_TIMEOUT", "custom-tool.run exceeded the requested timeout before the Unity Bridge returned a response.", [
          "Inspect AlpsUnityBridge.log and the requestId before retrying; the Editor may still be finishing a long-running tool.",
          "Use --job only as a preference marker in this release; Local Agent asynchronous custom-tool jobs are not implemented yet."
        ], { requestId, command, timeoutMs: typeof args.timeoutMs === "number" ? args.timeoutMs : 30_000, bridgeEndpoint: endpoint ?? null });
      }
      return failure(requestId, "BRIDGE_CONNECTION_FAILED", errorMessage(error), ["Check Unity Bridge endpoint and logs."]);
    }
  }, retryOptions);

  result.durationMs = durationMsFrom(startMs);
  return result;
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
  if (group === "asset" && ["search", "info", "create", "mkdir", "import", "modify", "delete", "duplicate", "move", "rename"].includes(command ?? "")) {
    return command === "mkdir" ? "asset.create" : `asset.${command}`;
  }
  if (group === "prefab" && ["info", "hierarchy", "create", "modify", "stage-open", "stage-save", "stage-close"].includes(command ?? "")) {
    return `prefab.${command}`;
  }
  if (group === "material" && ["info", "create", "assign", "set"].includes(command ?? "")) {
    return `material.${command}`;
  }
  if (group === "graphics" && ["quality", "settings", "screenshot", "pipeline", "volume"].includes(command ?? "")) {
    return `graphics.${command}`;
  }
  if (group === "physics" && ["settings", "raycast", "simulation"].includes(command ?? "")) {
    return `physics.${command}`;
  }
  if (group === "profiler" && ["start", "stop", "capture", "status"].includes(command ?? "")) {
    return `profiler.${command}`;
  }
  if (group === "probuilder" && ["shape", "create", "csg", "edit"].includes(command ?? "")) {
    return `probuilder.${command}`;
  }
  if (group === "custom-tool" && ["list", "run"].includes(command ?? "")) {
    return `custom-tool.${command}`;
  }
  if (group === "shader" && ["info", "read", "create", "update"].includes(command ?? "")) {
    return `shader.${command}`;
  }
  if (group === "texture" && ["info", "create", "sprite", "import-settings"].includes(command ?? "")) {
    return `texture.${command}`;
  }
  if (group === "animation" && ["clip", "state", "play", "stop"].includes(command ?? "")) {
    return `animation.${command}`;
  }
  if (group === "vfx" && ["graph", "effect"].includes(command ?? "")) {
    return `vfx.${command}`;
  }
  if (group === "ui" && ["canvas/create", "text/create", "button/create", "image/create", "document/info", "document/create", "hierarchy"].includes(command ?? "")) {
    return `ui.${command?.replaceAll("/", ".")}`;
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
  if (group === "editor" && command === "state") {
    return "editor.state";
  }
  if (group === "editor" && ["play", "pause", "stop"].includes(command ?? "")) {
    return `editor.${command}`;
  }
  if (group === "editor" && command === "menu") {
    return "editor.menu";
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
    return assetArgs(command, flags, positionalTarget);
  }

  if (command.startsWith("prefab.")) {
    if (command === "prefab.create") {
      return compactArgs({
        source: positionalTarget ?? stringFlag(flags, "source") ?? stringFlag(flags, "target"),
        path: stringFlag(flags, "path") ?? stringFlag(flags, "destination") ?? stringFlag(flags, "to"),
        overwrite: boolFlag(flags, "overwrite")
      });
    }
    if (command === "prefab.modify") {
      return compactArgs({
        path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "prefab"),
        name: stringFlag(flags, "name"),
        addChild: stringFlag(flags, "add-child"),
        removeChild: stringFlag(flags, "remove-child"),
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
    if (command === "graphics.pipeline") {
      return queryArgs(flags);
    }
    if (command === "graphics.volume") {
      return compactArgs({ ...queryArgs(flags), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") });
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

  if (command.startsWith("profiler.")) {
    return compactArgs({
      ...queryArgs(flags),
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "output"),
      module: stringFlag(flags, "module"),
      format: stringFlag(flags, "format"),
      durationMs: numberFlag(flags, "duration-ms"),
      frames: numberFlag(flags, "frames"),
      overwrite: boolFlag(flags, "overwrite"),
      deepProfiling: boolFlag(flags, "deep-profiling")
    });
  }

  if (command.startsWith("probuilder.")) {
    return compactArgs({
      ...(command === "probuilder.create" ? queryArgs(flags) : targetArgs(flags, positionalTarget)),
      shape: stringFlag(flags, "shape") ?? stringFlag(flags, "type") ?? (command === "probuilder.shape" ? positionalTarget : undefined),
      operation: stringFlag(flags, "operation") ?? stringFlag(flags, "op"),
      name: stringFlag(flags, "name"),
      parent: stringFlag(flags, "parent"),
      source: stringFlag(flags, "source"),
      x: floatFlag(flags, "x"),
      y: floatFlag(flags, "y"),
      z: floatFlag(flags, "z"),
      width: floatFlag(flags, "width"),
      height: floatFlag(flags, "height"),
      depth: floatFlag(flags, "depth"),
      radius: floatFlag(flags, "radius"),
      segments: numberFlag(flags, "segments"),
      sides: numberFlag(flags, "sides"),
      material: stringFlag(flags, "material"),
      value: stringFlag(flags, "value")
    });
  }

  if (command.startsWith("custom-tool.")) {
    return customToolArgs(command, flags, positionalTarget);
  }

  if (command.startsWith("shader.")) {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "shader"),
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "shader"),
      name: stringFlag(flags, "name"),
      content: await contentFlag(flags),
      overwrite: boolFlag(flags, "overwrite")
    });
  }

  if (command.startsWith("texture.")) {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "texture"),
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "texture"),
      source: stringFlag(flags, "source") ?? stringFlag(flags, "input"),
      overwrite: boolFlag(flags, "overwrite"),
      width: numberFlag(flags, "width"),
      height: numberFlag(flags, "height"),
      color: stringFlag(flags, "color"),
      textureType: stringFlag(flags, "texture-type") ?? stringFlag(flags, "type"),
      spriteMode: stringFlag(flags, "sprite-mode") ?? stringFlag(flags, "mode"),
      pixelsPerUnit: numberFlag(flags, "pixels-per-unit") ?? numberFlag(flags, "ppu"),
      pivotX: stringFlag(flags, "pivot-x"),
      pivotY: stringFlag(flags, "pivot-y"),
      readable: boolFlag(flags, "readable"),
      mipmap: boolFlag(flags, "mipmap"),
      sRGB: boolFlag(flags, "srgb"),
      alphaIsTransparency: boolFlag(flags, "alpha-is-transparency"),
      wrapMode: stringFlag(flags, "wrap-mode"),
      filterMode: stringFlag(flags, "filter-mode"),
      maxTextureSize: numberFlag(flags, "max-texture-size"),
      compression: stringFlag(flags, "compression")
    });
  }

  if (command.startsWith("animation.")) {
    if (command === "animation.clip") {
      return compactArgs({
        action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? (flags.create ? "create" : "info"),
        path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "clip"),
        overwrite: boolFlag(flags, "overwrite"),
        frameRate: floatFlag(flags, "frame-rate"),
        legacy: boolFlag(flags, "legacy")
      });
    }
    return compactArgs({
      ...targetArgs(flags, positionalTarget),
      state: stringFlag(flags, "state") ?? stringFlag(flags, "name"),
      layer: numberFlag(flags, "layer"),
      normalizedTime: floatFlag(flags, "normalized-time")
    });
  }

  if (command.startsWith("vfx.")) {
    if (command === "vfx.graph") {
      return compactArgs({
        action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? (flags.create ? "create" : "info"),
        path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "graph"),
        overwrite: boolFlag(flags, "overwrite")
      });
    }
    return compactArgs({
      ...targetArgs(flags, positionalTarget),
      graph: stringFlag(flags, "graph") ?? stringFlag(flags, "path"),
      add: boolFlag(flags, "add")
    });
  }

  if (command.startsWith("ui.")) {
    if (command === "ui.hierarchy") {
      return compactArgs({ ...queryArgs(flags), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") });
    }
    if (command === "ui.document.info") {
      const documentTarget = positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name");
      return compactArgs({
        target: documentTarget,
        path: stringFlag(flags, "path") ?? stringFlag(flags, "uxml") ?? (looksLikeUiAssetPath(documentTarget) ? documentTarget : undefined),
        uss: stringFlag(flags, "uss") ?? stringFlag(flags, "style")
      });
    }
    if (command === "ui.document.create") {
      return compactArgs({
        target: positionalTarget ?? stringFlag(flags, "target"),
        name: stringFlag(flags, "name"),
        uxml: stringFlag(flags, "uxml") ?? stringFlag(flags, "path"),
        uss: stringFlag(flags, "uss") ?? stringFlag(flags, "style"),
        panelSettings: stringFlag(flags, "panel-settings"),
        overwrite: boolFlag(flags, "overwrite")
      });
    }
    return compactArgs({
      name: positionalTarget ?? stringFlag(flags, "name"),
      parent: stringFlag(flags, "parent") ?? stringFlag(flags, "canvas"),
      text: stringFlag(flags, "text") ?? stringFlag(flags, "label"),
      sprite: stringFlag(flags, "sprite") ?? stringFlag(flags, "path"),
      color: stringFlag(flags, "color"),
      width: numberFlag(flags, "width"),
      height: numberFlag(flags, "height"),
      x: numberFlag(flags, "x"),
      y: numberFlag(flags, "y"),
      fontSize: numberFlag(flags, "font-size") ?? numberFlag(flags, "fontSize")
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

  if (command === "editor.state") {
    return compactArgs({ ...queryArgs(flags) });
  }

  if (command === "editor.play" || command === "editor.pause" || command === "editor.stop") {
    return compactArgs({ ...queryArgs(flags), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
  }

  if (command === "editor.menu") {
    return compactArgs({ ...queryArgs(flags), path: positionalTarget ?? stringFlag(flags, "path"), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
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

function bridgeEndpoint(flags: Record<string, string | boolean>, runtime: BridgeRuntime, selectedInstance?: InstanceSelection): string | undefined {
  return stringFlag(flags, "bridge-endpoint") ?? runtime.bridgeEndpoint ?? process.env.ALPS_UNITY_BRIDGE_ENDPOINT ?? selectedInstance?.instance.bridgeEndpoint;
}

function selectedInstanceSummary(selection: InstanceSelection): Record<string, string> {
  return {
    matchedBy: selection.matchedBy,
    instanceId: selection.instance.instanceId,
    projectPath: selection.instance.projectPath,
    projectHash: selection.instance.projectHash,
    bridgeEndpoint: selection.instance.bridgeEndpoint
  };
}

async function readUiDocumentOffline(requestId: string, command: string, path: string): Promise<CliResult> {
  const documentPath = resolve(path);
  try {
    const documentStat = await stat(documentPath);
    if (!documentStat.isFile()) {
      return failure(requestId, "E_UI_DOCUMENT_PATH_INVALID", `UI document path is not a file: ${path}`, ["Pass a .uxml or .uss file path."]);
    }
    const content = await readFile(documentPath, "utf8");
    const extension = documentPath.toLowerCase().endsWith(".uss") ? "uss" : documentPath.toLowerCase().endsWith(".uxml") ? "uxml" : "unknown";
    const diagnostics = uiDocumentDiagnostics(content, extension);
    return success(requestId, {
      command,
      mode: "offline-reader",
      document: {
        path: documentPath,
        kind: extension,
        bytes: Buffer.byteLength(content, "utf8"),
        lines: content.length === 0 ? 0 : content.split(/\r?\n/).length
      },
      diagnostics
    });
  } catch (error) {
    return failure(requestId, "E_UI_DOCUMENT_NOT_FOUND", `UI document file not found or unreadable: ${path}`, [error instanceof Error ? error.message : "Check the path and permissions."]);
  }
}

function uiDocumentDiagnostics(content: string, extension: string): Array<Record<string, string>> {
  const diagnostics: Array<Record<string, string>> = [];
  if (extension === "unknown") {
    diagnostics.push({ level: "warning", code: "UI_DOCUMENT_EXTENSION_UNKNOWN", message: "Expected a .uxml or .uss file." });
  }
  if (extension === "uxml" && !/<ui:UXML|<UXML|<engine:UXML/.test(content)) {
    diagnostics.push({ level: "warning", code: "UI_UXML_ROOT_MISSING", message: "UXML root element was not detected." });
  }
  if (extension === "uss" && !/[.#]?[A-Za-z_][\w-]*\s*\{[^}]*\}/m.test(content)) {
    diagnostics.push({ level: "warning", code: "UI_USS_RULE_MISSING", message: "USS style rule was not detected." });
  }
  if (diagnostics.length === 0) {
    diagnostics.push({ level: "info", code: "UI_DOCUMENT_READ_OK", message: "UI document file is readable and passed basic checks." });
  }
  return diagnostics;
}

function looksLikeUiAssetPath(value: string | undefined): boolean {
  return typeof value === "string" && /\.(uxml|uss)$/i.test(value);
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

function bridgeErrorDetails(error: BridgeError | undefined): Record<string, unknown> {
  if (!error) {
    return {};
  }
  const { code: _code, message: _message, details, ...rest } = error;
  if (details !== undefined && typeof details === "object" && !Array.isArray(details) && Object.keys(rest).length === 0) {
    return details as Record<string, unknown>;
  }
  return details === undefined ? rest : { ...rest, details };
}

function bridgeHttpTimeoutMs(payload: BridgeRequest): number {
  if (payload.command === "code.exec" && typeof payload.args.timeoutMs === "number" && Number.isFinite(payload.args.timeoutMs) && payload.args.timeoutMs > 0) {
    return payload.args.timeoutMs + 5000;
  }
  if (payload.command === "custom-tool.run") {
    const timeoutMs = typeof payload.args.timeoutMs === "number" && Number.isFinite(payload.args.timeoutMs) ? payload.args.timeoutMs : 30_000;
    return Math.max(timeoutMs + 5000, 65_000);
  }
  return 10_000;
}

function liveCodeBusyDetails(requestId: string, endpoint: string | undefined, args: Record<string, unknown>, details: Record<string, unknown>): Record<string, unknown> {
  const timeoutMs = typeof args.timeoutMs === "number" && Number.isFinite(args.timeoutMs) ? args.timeoutMs : null;
  const logPath = typeof details.logPath === "string" ? details.logPath : "Unity project Logs/AlpsUnityBridge.log";
  return {
    requestId,
    command: "code.exec",
    channel: "live",
    timeoutMs,
    bridgeEndpoint: endpoint ?? null,
    audit: { requestId, command: "code.exec", channel: "live", timeoutMs },
    logs: { bridge: logPath },
    ...details
  };
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

async function assetArgs(command: string, flags: Record<string, string | boolean>, positionalTarget: string | undefined): Promise<Record<string, unknown>> {
  if (command === "asset.rename") {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "source"),
      name: stringFlag(flags, "name") ?? stringFlag(flags, "new-name")
    });
  }

  if (command === "asset.duplicate" || command === "asset.move") {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "source"),
      source: stringFlag(flags, "source"),
      destination: stringFlag(flags, "destination") ?? stringFlag(flags, "to")
    });
  }

  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "source"),
    source: stringFlag(flags, "source"),
    destination: stringFlag(flags, "destination") ?? stringFlag(flags, "to"),
    type: stringFlag(flags, "type"),
    content: await contentFlag(flags),
    overwrite: boolFlag(flags, "overwrite")
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

async function customToolArgs(command: string, flags: Record<string, string | boolean>, positionalTarget: string | undefined): Promise<Record<string, unknown>> {
  const projectPath = resolve(stringFlag(flags, "project") ?? process.cwd());
  const instanceId = stringFlag(flags, "instance");

  if (command === "custom-tool.list") {
    return compactArgs({ projectPath, instanceId });
  }

  const name = positionalTarget ?? stringFlag(flags, "name");
  if (!name) {
    throw new BridgeArgumentError("E_TOOL_NOT_FOUND", "Pass a custom tool name: unity_cli custom-tool run <name>.", ["Run unity_cli custom-tool list to inspect registered tools."]);
  }

  const timeoutMs = timeoutFlag(flags, 30_000, 600_000);
  const artifactDir = stringFlag(flags, "artifact-dir");
  return compactArgs({
    projectPath,
    instanceId,
    name,
    input: await customToolInput(flags),
    timeoutMs,
    artifactDir: artifactDir ? resolve(artifactDir) : undefined,
    execution: { preferJob: boolFlag(flags, "job") === true }
  });
}

async function customToolInput(flags: Record<string, string | boolean>): Promise<unknown> {
  const sources = [stringFlag(flags, "input") !== undefined, stringFlag(flags, "input-file") !== undefined, boolFlag(flags, "stdin") === true].filter(Boolean).length;
  if (sources > 1) {
    throw new BridgeArgumentError("E_SCHEMA_INVALID", "Pass only one of --input, --input-file or --stdin for custom-tool run.");
  }

  let raw = stringFlag(flags, "input");
  const inputFile = stringFlag(flags, "input-file");
  if (inputFile) {
    try {
      raw = await readFile(resolve(inputFile), "utf8");
    } catch (error) {
      throw new BridgeArgumentError("E_PATH_INVALID", error instanceof Error ? error.message : String(error));
    }
  }
  if (boolFlag(flags, "stdin") === true) {
    raw = await readStdin();
  }
  if (raw === undefined || raw.trim() === "") {
    return {};
  }
  try {
    return JSON.parse(raw) as unknown;
  } catch (error) {
    throw new BridgeArgumentError("E_SCHEMA_INVALID", `custom-tool input must be valid JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
}

function timeoutFlag(flags: Record<string, string | boolean>, fallback: number, max: number): number {
  const raw = stringFlag(flags, "timeout-ms");
  if (!raw) {
    return fallback;
  }
  const parsed = Number(raw);
  if (!Number.isInteger(parsed) || parsed <= 0 || parsed > max) {
    throw new BridgeArgumentError("E_SCHEMA_INVALID", `--timeout-ms must be an integer between 1 and ${max}.`);
  }
  return parsed;
}

async function readStdin(): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of stdin) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }
  return Buffer.concat(chunks).toString("utf8");
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
