import { animationBridgeSpecs } from "./domains/animation.js";
import { assetBridgeSpecs } from "./domains/asset.js";
import { buildBridgeSpecs } from "./domains/build.js";
import { cameraBridgeSpecs } from "./domains/camera.js";
import { codeBridgeSpecs } from "./domains/code.js";
import { consoleBridgeSpecs } from "./domains/console.js";
import { contextBridgeSpecs } from "./domains/context.js";
import { diagnoseBridgeSpecs } from "./domains/diagnose.js";
import { editorBridgeSpecs } from "./domains/editor.js";
import { extensionBridgeSpecs } from "./domains/extension.js";
import { gameObjectBridgeSpecs } from "./domains/game-object.js";
import { graphicsBridgeSpecs } from "./domains/graphics.js";
import { materialBridgeSpecs } from "./domains/material.js";
import { objectBridgeSpecs } from "./domains/object.js";
import { packageBridgeSpecs } from "./domains/package.js";
import { physicsBridgeSpecs } from "./domains/physics.js";
import { prefabBridgeSpecs } from "./domains/prefab.js";
import { profilerBridgeSpecs } from "./domains/profiler.js";
import { reflectionBridgeSpecs } from "./domains/reflection.js";
import { sceneBridgeSpecs } from "./domains/scene.js";
import { screenshotBridgeSpecs } from "./domains/screenshot.js";
import { scriptableObjectBridgeSpecs } from "./domains/scriptable-object.js";
import { shaderBridgeSpecs } from "./domains/shader.js";
import { textureBridgeSpecs } from "./domains/texture.js";
import { uiBridgeSpecs } from "./domains/ui.js";
import { projectToolBridgeSpecs } from "./domains/project-tool.js";
import { runtimeToolBridgeSpecs } from "./domains/runtime-tool.js";
import { securityPolicyForCommand } from "../security-policy.js";
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
  ...graphicsBridgeSpecs,
  ...physicsBridgeSpecs,
  ...profilerBridgeSpecs,
  ...shaderBridgeSpecs,
  ...textureBridgeSpecs,
  ...animationBridgeSpecs,
  ...uiBridgeSpecs,
  ...editorBridgeSpecs,
  ...diagnoseBridgeSpecs,
  ...codeBridgeSpecs,
  ...objectBridgeSpecs,
  ...reflectionBridgeSpecs,
  ...screenshotBridgeSpecs,
  ...consoleBridgeSpecs,
  ...packageBridgeSpecs,
  ...buildBridgeSpecs,
  ...runtimeToolBridgeSpecs,
  ...projectToolBridgeSpecs,
...contextBridgeSpecs,
  ...extensionBridgeSpecs
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
      const policy = securityPolicyForCommand(spec.bridgeCommand);
      if (policy) {
        spec.destructiveHint = policy.destructiveHint;
        spec.supportsDryRun = policy.supportsDryRun;
      }
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
    return this.byBridgeCommand.get(bridgeCommand)?.[0];
  }

  public specs(): BridgeCommandSpec[] {
    return [...this.byCliCommand.values()];
  }

  private static cliKey(group: string, command: string): string {
    return `${group}\u0000${command}`;
  }
}

export const bridgeCommandRegistry = new BridgeCommandRegistry(CORE_DOMAIN_SPECS);
