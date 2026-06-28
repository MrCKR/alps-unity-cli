import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

function structuredInput(flags: Record<string, unknown>): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function boolValue(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}

export const buildBridgeSpecs: BridgeCommandSpec[] = [
  { group: "build", command: "scenes", bridgeCommand: "build.scenes", buildArgs: buildScenesArgs },
  { group: "build", command: "platform", bridgeCommand: "build.platform", buildArgs: buildPlatformArgs },
  { group: "build", command: "settings", bridgeCommand: "build.settings", buildArgs: buildSettingsArgs },
  { group: "build", command: "profiles", bridgeCommand: "build.profiles", buildArgs: buildProfilesArgs, supportsDryRun: true }
];

function buildProfilesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const s = structuredInput(flags);
  return compactArgs({
    action: stringFlag(flags, "action") ?? stringValue(s.action),
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringValue(s.path),
    name: stringFlag(flags, "name") ?? stringValue(s.name),
    target: stringFlag(flags, "target") ?? stringValue(s.target),
    dryRun: boolFlag(flags, "dry-run") ?? boolValue(s.dryRun)
  });
}

function buildSettingsArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const s = structuredInput(flags);
  return compactArgs({
    action: stringFlag(flags, "action") ?? stringValue(s.action),
    productName: stringFlag(flags, "product-name") ?? stringValue(s.productName),
    bundleVersion: stringFlag(flags, "bundle-version") ?? stringValue(s.bundleVersion),
    companyId: stringFlag(flags, "company-id") ?? stringValue(s.companyId),
    applicationIdentifier: stringFlag(flags, "application-identifier") ?? stringValue(s.applicationIdentifier),
    scriptingBackend: stringFlag(flags, "scripting-backend") ?? stringValue(s.scriptingBackend),
    apiCompatibilityLevel: stringFlag(flags, "api-compatibility-level") ?? stringValue(s.apiCompatibilityLevel),
    dryRun: boolFlag(flags, "dry-run") ?? boolValue(s.dryRun)
  });
}

function buildScenesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ action: stringFlag(flags, "action"), path: positionalTarget ?? stringFlag(flags, "path"), dryRun: boolFlag(flags, "dry-run") });
}

function buildPlatformArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ action: stringFlag(flags, "action"), target: positionalTarget ?? stringFlag(flags, "target"), dryRun: boolFlag(flags, "dry-run") });
}
