import { animationBridgeSpecs } from "./domains/animation.js";
import { assetBridgeSpecs } from "./domains/asset.js";
import { cameraBridgeSpecs } from "./domains/camera.js";
import { cinemachineBridgeSpecs } from "./domains/cinemachine.js";
import { codeBridgeSpecs } from "./domains/code.js";
import { diagnoseBridgeSpecs } from "./domains/diagnose.js";
import { editorBridgeSpecs } from "./domains/editor.js";
import { gameObjectBridgeSpecs } from "./domains/game-object.js";
import { graphicsBridgeSpecs } from "./domains/graphics.js";
import { hdrpBridgeSpecs } from "./domains/hdrp.js";
import { materialBridgeSpecs } from "./domains/material.js";
import { physicsBridgeSpecs } from "./domains/physics.js";
import { prefabBridgeSpecs } from "./domains/prefab.js";
import { probuilderBridgeSpecs } from "./domains/probuilder.js";
import { profilerBridgeSpecs } from "./domains/profiler.js";
import { sceneBridgeSpecs } from "./domains/scene.js";
import { scriptBridgeSpecs } from "./domains/script.js";
import { shaderBridgeSpecs } from "./domains/shader.js";
import { textureBridgeSpecs } from "./domains/texture.js";
import { uiBridgeSpecs } from "./domains/ui.js";
import { urpBridgeSpecs } from "./domains/urp.js";
import { vfxBridgeSpecs } from "./domains/vfx.js";
import { contextBridgeSpecs } from "./domains/context.js";
import { extensionBridgeSpecs } from "./domains/extension.js";
import { projectToolBridgeSpecs } from "./domains/project-tool.js";
import { runtimeToolBridgeSpecs } from "./domains/runtime-tool.js";
import { securityPolicyForCommand } from "../security-policy.js";
import type { BridgeCommandSpec } from "./types.js";

const DOMAIN_SPECS: BridgeCommandSpec[] = [
  ...sceneBridgeSpecs,
  ...cameraBridgeSpecs,
  ...gameObjectBridgeSpecs,
  ...assetBridgeSpecs,
  ...prefabBridgeSpecs,
  ...materialBridgeSpecs,
  ...graphicsBridgeSpecs,
  ...physicsBridgeSpecs,
  ...profilerBridgeSpecs,
  ...probuilderBridgeSpecs,
  ...cinemachineBridgeSpecs,
  ...shaderBridgeSpecs,
  ...textureBridgeSpecs,
  ...animationBridgeSpecs,
  ...vfxBridgeSpecs,
  ...urpBridgeSpecs,
  ...hdrpBridgeSpecs,
  ...uiBridgeSpecs,
  ...scriptBridgeSpecs,
  ...editorBridgeSpecs,
  ...diagnoseBridgeSpecs,
  ...codeBridgeSpecs,
  ...runtimeToolBridgeSpecs,
  ...projectToolBridgeSpecs,
  ...contextBridgeSpecs,
  ...extensionBridgeSpecs
];

export class BridgeCommandRegistry {
  private readonly byCliCommand = new Map<string, BridgeCommandSpec>();
  private readonly byBridgeCommand = new Map<string, BridgeCommandSpec[]>();

  public constructor(specs: BridgeCommandSpec[]) {
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

export const bridgeCommandRegistry = new BridgeCommandRegistry(DOMAIN_SPECS);
