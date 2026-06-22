import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import type { BridgeFlags } from "./types.js";

export class BridgeArgumentError extends Error {
  public constructor(public readonly code: string, message: string, public readonly nextActions: string[] = []) {
    super(message);
  }
}

export function screenshotArgs(flags: BridgeFlags, positionalPath: string | undefined): Record<string, unknown> {
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

export function queryArgs(flags: BridgeFlags): Record<string, unknown> {
  return compactArgs({ pageSize: numberFlag(flags, "page-size") ?? numberFlag(flags, "limit"), cursor: stringFlag(flags, "cursor"), filter: stringFlag(flags, "filter") });
}

export function targetArgs(flags: BridgeFlags, positionalTarget: string | undefined): Record<string, unknown> {
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

export async function assetArgs(command: string, flags: BridgeFlags, positionalTarget: string | undefined): Promise<Record<string, unknown>> {
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
    overwrite: boolFlag(flags, "overwrite"),
    dryRun: boolFlag(flags, "dry-run") ?? boolFlag(flags, "preflight")
  });
}



export function stringFlag(flags: BridgeFlags, key: string): string | undefined {
  return typeof flags[key] === "string" ? flags[key] : undefined;
}

export function boolFlag(flags: BridgeFlags, key: string): boolean | string | undefined {
  if (flags[key] === true || flags[key] === "true") {
    return true;
  }
  if (flags[key] === "false") {
    return false;
  }
  return undefined;
}

export async function contentFlag(flags: BridgeFlags): Promise<string | undefined> {
  const inline = stringFlag(flags, "content");
  if (inline !== undefined) {
    return inline;
  }
  const filePath = stringFlag(flags, "content-file");
  return filePath ? readFile(resolve(filePath), "utf8") : undefined;
}

export function numberFlag(flags: BridgeFlags, key: string): number | undefined {
  const value = stringFlag(flags, key);
  if (!value) {
    return undefined;
  }
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : undefined;
}

export function floatFlag(flags: BridgeFlags, key: string): number | undefined {
  const value = stringFlag(flags, key);
  if (!value) {
    return undefined;
  }
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

export function compactArgs(args: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(args).filter(([, value]) => value !== undefined));
}
