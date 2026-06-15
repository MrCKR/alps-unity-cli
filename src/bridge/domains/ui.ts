import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const uiBridgeSpecs: BridgeCommandSpec[] = [
  { group: "ui", command: "canvas/create", bridgeCommand: "ui.canvas.create", buildArgs: uiArgs },
  { group: "ui", command: "text/create", bridgeCommand: "ui.text.create", buildArgs: uiArgs },
  { group: "ui", command: "button/create", bridgeCommand: "ui.button.create", buildArgs: uiArgs },
  { group: "ui", command: "image/create", bridgeCommand: "ui.image.create", buildArgs: uiArgs },
  { group: "ui", command: "document/info", bridgeCommand: "ui.document.info", buildArgs: uiArgs },
  { group: "ui", command: "document/create", bridgeCommand: "ui.document.create", buildArgs: uiArgs },
  { group: "ui", command: "hierarchy", bridgeCommand: "ui.hierarchy", buildArgs: uiArgs }
];

function uiArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
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

function looksLikeUiAssetPath(value: string | undefined): boolean {
  return typeof value === "string" && /\.(uxml|uss)$/i.test(value);
}
