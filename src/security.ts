import { createHash } from "node:crypto";
import { createInterface } from "node:readline/promises";
import { stdin, stdout } from "node:process";
import { getBoolean } from "./args.js";
import { failure } from "./output.js";
import type { CliResult } from "./types.js";

export const CONFIRMED_FLAG = "__destructiveConfirmed";

export interface SecurityProof {
  confirmed: boolean;
  toolName: string;
  descriptorFingerprint: string;
  inputSha256: string;
}

export interface DestructiveGuardOptions {
  command: string;
  summary?: string;
  destructiveHint?: boolean;
  supportsDryRun?: boolean;
  requestId: string;
}

export function isDryRunRequested(flags: Record<string, string | boolean>): boolean {
  return getBoolean(flags, "dry-run") || getBoolean(flags, "preflight");
}

export function isDestructiveConfirmed(flags: Record<string, string | boolean>): boolean {
  return getBoolean(flags, "yes") || flags[CONFIRMED_FLAG] === true;
}

export async function confirmDestructiveExecution(
  flags: Record<string, string | boolean>,
  options: DestructiveGuardOptions
): Promise<CliResult | undefined> {
  if (!options.destructiveHint) {
    return undefined;
  }

  if (isDryRunRequested(flags)) {
    if (!options.supportsDryRun) {
      return failure(options.requestId, "E_DRY_RUN_UNSUPPORTED", `Dry-run is not supported for destructive command: ${options.command}.`, [
        "Remove --dry-run/--preflight for live execution, or use a command that declares supportsDryRun=true."
      ], {
        command: options.command,
        supportsDryRun: false
      });
    }
    return undefined;
  }

  if (isDestructiveConfirmed(flags)) {
    flags[CONFIRMED_FLAG] = true;
    return undefined;
  }

  if (stdin.isTTY && stdout.isTTY && process.env.CI !== "true") {
    const label = options.summary ? `${options.command} (${options.summary})` : options.command;
    const answer = await promptConfirmation(`Destructive Unity CLI command ${label} requires confirmation. Type "yes" to continue: `);
    if (answer.trim().toLowerCase() === "yes") {
      flags[CONFIRMED_FLAG] = true;
      return undefined;
    }
  }

  return failure(options.requestId, "E_DESTRUCTIVE_CONFIRMATION_REQUIRED", `Destructive command requires explicit confirmation: ${options.command}.`, [
    "Review the tool metadata/destructiveHint and planned target first.",
    "Rerun with --yes to execute, or add --dry-run when the tool supports preview."
  ], {
    command: options.command,
    summary: options.summary ?? null,
    destructiveHint: true,
    supportsDryRun: options.supportsDryRun === true,
    dryRunFlag: "--dry-run",
    confirmationFlag: "--yes"
  });
}

export function canonicalJson(value: unknown): string {
  if (value === null) return "null";
  if (typeof value === "string" || typeof value === "boolean") return JSON.stringify(value);
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new TypeError("Canonical JSON does not support non-finite numbers.");
    return Object.is(value, -0) ? "0" : JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (typeof value === "object") {
    const record = value as Record<string, unknown>;
    const entries = Object.keys(record)
      .filter((key) => record[key] !== undefined)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${canonicalJson(record[key])}`);
    return `{${entries.join(",")}}`;
  }
  throw new TypeError(`Canonical JSON does not support ${typeof value}.`);
}

export function sha256CanonicalJson(value: unknown): string {
  return createHash("sha256").update(canonicalJson(value), "utf8").digest("hex");
}

export function destructiveSecurityProof(
  flags: Record<string, string | boolean>,
  toolName: string,
  descriptor: unknown,
  input: unknown
): SecurityProof {
  return {
    confirmed: isDestructiveConfirmed(flags),
    toolName,
    descriptorFingerprint: sha256CanonicalJson(descriptor),
    inputSha256: sha256CanonicalJson(input)
  };
}

async function promptConfirmation(question: string): Promise<string> {
  const rl = createInterface({ input: stdin, output: stdout });
  try {
    return await rl.question(question);
  } finally {
    rl.close();
  }
}
