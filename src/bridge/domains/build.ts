import { compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

function structuredInput(flags: Record<string, unknown>): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try { const parsed = JSON.parse(raw); return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {}; } catch { return {}; }
}
function stringValue(value: unknown): string | undefined { return typeof value === "string" ? value : undefined; }

export const buildBridgeSpecs: BridgeCommandSpec[] = [
  { group: "build", command: "scene.list", bridgeCommand: "catalog.editor.run", toolName: "build.scene.list", buildArgs: emptyArgs },
  { group: "build", command: "scene.add", bridgeCommand: "catalog.editor.run", toolName: "build.scene.add", buildArgs: pathArgs, destructiveHint: true, supportsDryRun: true },
  { group: "build", command: "scene.remove", bridgeCommand: "catalog.editor.run", toolName: "build.scene.remove", buildArgs: pathArgs, destructiveHint: true, supportsDryRun: true },
  { group: "build", command: "scene.enable", bridgeCommand: "catalog.editor.run", toolName: "build.scene.enable", buildArgs: pathArgs, destructiveHint: true, supportsDryRun: true },
  { group: "build", command: "scene.disable", bridgeCommand: "catalog.editor.run", toolName: "build.scene.disable", buildArgs: pathArgs, destructiveHint: true, supportsDryRun: true },
  { group: "build", command: "platform.get", bridgeCommand: "catalog.editor.run", toolName: "build.platform.get", buildArgs: emptyArgs },
  { group: "build", command: "platform.set", bridgeCommand: "catalog.editor.run", toolName: "build.platform.set", buildArgs: targetArgs, destructiveHint: true, supportsDryRun: true },
  { group: "build", command: "profile.list", bridgeCommand: "catalog.editor.run", toolName: "build.profile.list", buildArgs: emptyArgs },
  { group: "build", command: "profile.create", bridgeCommand: "catalog.editor.run", toolName: "build.profile.create", buildArgs: profileArgs, destructiveHint: true, supportsDryRun: true },
  { group: "build", command: "profile.switch", bridgeCommand: "catalog.editor.run", toolName: "build.profile.switch", buildArgs: pathArgs, destructiveHint: true, supportsDryRun: true },
  { group: "build", command: "profile.modify", bridgeCommand: "catalog.editor.run", toolName: "build.profile.modify", buildArgs: profileArgs, destructiveHint: true, supportsDryRun: true },
  { group: "build", command: "settings.get", bridgeCommand: "catalog.editor.run", toolName: "build.settings.get", buildArgs: emptyArgs },
  { group: "build", command: "settings.set", bridgeCommand: "catalog.editor.run", toolName: "build.settings.set", buildArgs: settingsArgs, destructiveHint: true, supportsDryRun: true }
];

function emptyArgs(): Record<string, unknown> { return {}; }
function pathArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> { const s = structuredInput(flags); return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path") ?? stringValue(s.path) }); }
function targetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> { const s = structuredInput(flags); return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(s.target) }); }
function profileArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> { const s = structuredInput(flags); return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path") ?? stringValue(s.path), name: stringFlag(flags, "name") ?? stringValue(s.name) }); }
function settingsArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> { const s = structuredInput(flags); return compactArgs({ productName: stringFlag(flags, "product-name") ?? stringValue(s.productName), bundleVersion: stringFlag(flags, "bundle-version") ?? stringValue(s.bundleVersion), companyId: stringFlag(flags, "company-id") ?? stringValue(s.companyId), applicationIdentifier: stringFlag(flags, "application-identifier") ?? stringValue(s.applicationIdentifier), scriptingBackend: stringFlag(flags, "scripting-backend") ?? stringValue(s.scriptingBackend), apiCompatibilityLevel: stringFlag(flags, "api-compatibility-level") ?? stringValue(s.apiCompatibilityLevel) }); }
