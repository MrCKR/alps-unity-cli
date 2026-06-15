import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { stdin } from "node:process";
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
    overwrite: boolFlag(flags, "overwrite")
  });
}

export async function customToolArgs(command: string, flags: BridgeFlags, positionalTarget: string | undefined): Promise<Record<string, unknown>> {
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

async function customToolInput(flags: BridgeFlags): Promise<unknown> {
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

function timeoutFlag(flags: BridgeFlags, fallback: number, max: number): number {
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
