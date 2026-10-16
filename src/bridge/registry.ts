import { animationBridgeSpecs } from "./domains/animation.js";
import { assetBridgeSpecs } from "./domains/asset.js";
import { buildBridgeSpecs } from "./domains/build.js";
import { cameraBridgeSpecs } from "./domains/camera.js";
import { debugBridgeSpecs } from "./domains/debug.js";
import { doctorBridgeSpecs } from "./domains/doctor.js";
import { editorBridgeSpecs } from "./domains/editor.js";
import { gameObjectBridgeSpecs } from "./domains/game-object.js";
import { lightingBridgeSpecs } from "./domains/lighting.js";
import { materialBridgeSpecs } from "./domains/material.js";
import { navMeshBridgeSpecs } from "./domains/navmesh.js";
import { occlusionBridgeSpecs } from "./domains/occlusion.js";
import { physicsBridgeSpecs } from "./domains/physics.js";
import { prefabBridgeSpecs } from "./domains/prefab.js";
import { profilerBridgeSpecs } from "./domains/profiler.js";
import { renderingBridgeSpecs } from "./domains/rendering.js";
import { sceneBridgeSpecs } from "./domains/scene.js";
import { screenshotBridgeSpecs } from "./domains/screenshot.js";
import { scriptableObjectBridgeSpecs } from "./domains/scriptable-object.js";
import { serializedObjectBridgeSpecs } from "./domains/serialized-object.js";
import { shaderBridgeSpecs } from "./domains/shader.js";
import { textureBridgeSpecs } from "./domains/texture.js";
import { timelineBridgeSpecs } from "./domains/timeline.js";
import { uiBridgeSpecs } from "./domains/ui.js";
import { volumeBridgeSpecs } from "./domains/volume.js";
import { catalogEditorBridgeSpecs } from "./domains/catalog-editor.js";
import { catalogPlayerBridgeSpecs } from "./domains/catalog-player.js";
import type { BridgeCommandSpec } from "./types.js";

/**
 * Core domain specs — ProBuilder, Cinemachine, VFX, URP, HDRP are excluded from core.
 * They are registered via the extension contribution loader instead.
 */
const CORE_DOMAIN_SPECS: BridgeCommandSpec[] = [
  ...sceneBridgeSpecs,
  ...cameraBridgeSpecs,
  ...gameObjectBridgeSpecs,
  ...scriptableObjectBridgeSpecs,
  ...assetBridgeSpecs,
  ...prefabBridgeSpecs,
  ...materialBridgeSpecs,
  ...lightingBridgeSpecs,
  ...renderingBridgeSpecs,
  ...volumeBridgeSpecs,
  ...physicsBridgeSpecs,
  ...profilerBridgeSpecs,
  ...shaderBridgeSpecs,
  ...textureBridgeSpecs,
  ...timelineBridgeSpecs,
  ...navMeshBridgeSpecs,
  ...occlusionBridgeSpecs,
  ...animationBridgeSpecs,
  ...uiBridgeSpecs,
  ...editorBridgeSpecs,
  ...doctorBridgeSpecs,
  ...debugBridgeSpecs,
  ...serializedObjectBridgeSpecs,
  ...screenshotBridgeSpecs,
  ...buildBridgeSpecs,
  ...catalogPlayerBridgeSpecs,
  ...catalogEditorBridgeSpecs
];

export class BridgeCommandRegistry {
  private readonly byCliCommand = new Map<string, BridgeCommandSpec>();
  private readonly byBridgeCommand = new Map<string, BridgeCommandSpec[]>();

  public constructor(specs: BridgeCommandSpec[]) {
    this.registerSpecs(specs);
  }

  /**
   * Dynamically register bridge command specs.
   *
   * Used by the extension contribution loader to register extension-provided
   * bridge commands without statically importing them into the core
   * DOMAIN_SPECS aggregation.
   */
  public registerSpecs(specs: BridgeCommandSpec[]): void {
    for (const spec of specs) {
      const cliKey = BridgeCommandRegistry.cliKey(spec.group, spec.command);
      if (this.byCliCommand.has(cliKey)) {
        throw new Error(`Duplicate bridge CLI command registration: ${cliKey}`);
      }
      this.byCliCommand.set(cliKey, spec);

      const existingBridgeSpecs = this.byBridgeCommand.get(spec.bridgeCommand) ?? [];
      if (existingBridgeSpecs.some((existing) => existing.group === spec.group && existing.command === spec.command)) {
        throw new Error(`Duplicate bridge command spec: ${spec.bridgeCommand}`);
      }
      existingBridgeSpecs.push(spec);
      this.byBridgeCommand.set(spec.bridgeCommand, existingBridgeSpecs);
    }
  }

  public lookup(group: string | undefined, command: string | undefined): BridgeCommandSpec | undefined {
    if (!group) {
      return undefined;
    }
    return this.byCliCommand.get(BridgeCommandRegistry.cliKey(group, command ?? ""));
  }

  public lookupBridgeCommand(bridgeCommand: string): BridgeCommandSpec | undefined {
    const specs = this.byBridgeCommand.get(bridgeCommand);
    if (!specs || specs.length === 0) return undefined;
    // 优先返回通用 spec(无 toolName),避免工具特定 spec(如 scene.active 的 catalog.editor.run)被误用于通用路由
    return specs.find((s) => !s.toolName) ?? specs[0];
  }

  public specs(): BridgeCommandSpec[] {
    return [...this.byCliCommand.values()];
  }

  private static cliKey(group: string, command: string): string {
    return `${group}\u0000${command}`;
  }
}

export const bridgeCommandRegistry = new BridgeCommandRegistry(CORE_DOMAIN_SPECS);
