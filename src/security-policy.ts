export interface SecurityPolicyEntry {
  command: string;
  summary: string;
  destructiveHint: boolean;
  supportsDryRun?: boolean;
}

const SECURITY_POLICY: SecurityPolicyEntry[] = [
  { command: "asset.delete", summary: "Delete Asset", destructiveHint: true, supportsDryRun: true },
  { command: "texture.modify", summary: "Modify Texture", destructiveHint: true, supportsDryRun: true },
  { command: "texture.delete", summary: "Delete Texture", destructiveHint: true, supportsDryRun: true },
  { command: "shader.delete", summary: "Delete Shader", destructiveHint: true, supportsDryRun: true },
  { command: "texture.generate", summary: "Generate Texture", destructiveHint: true, supportsDryRun: true },
  { command: "scene.save", summary: "Save Scene", destructiveHint: true, supportsDryRun: true },
  { command: "code.exec", summary: "Execute Live Code", destructiveHint: true, supportsDryRun: true },
  { command: "object.modify", summary: "Modify Object", destructiveHint: true, supportsDryRun: true },
  { command: "reflection.method.call", summary: "Call Method via Reflection", destructiveHint: true },
  { command: "probuilder.delete-faces", summary: "ProBuilder Delete Faces", destructiveHint: true },
  { command: "probuilder.detach-faces", summary: "ProBuilder Detach Faces", destructiveHint: true },
  { command: "probuilder.combine-meshes", summary: "ProBuilder Combine Meshes", destructiveHint: true },
  { command: "probuilder.merge-objects", summary: "ProBuilder Merge Objects", destructiveHint: true },
  { command: "probuilder.flip-normals", summary: "ProBuilder Flip Normals", destructiveHint: true },
  { command: "cinemachine.extension", summary: "Cinemachine Extension Remove", destructiveHint: true },
  { command: "urp.feature", summary: "URP Renderer Feature Modify", destructiveHint: true },
  { command: "editor.menu", summary: "Execute Editor Menu", destructiveHint: true },
  { command: "package.install", summary: "Package Install", destructiveHint: true },
  { command: "package.remove", summary: "Package Remove", destructiveHint: true },
  { command: "batch.run", summary: "Batch Run", destructiveHint: true, supportsDryRun: true },
  { command: "instances.prune", summary: "Prune Unity Instances", destructiveHint: true },
  { command: "jobs.cancel", summary: "Cancel Job", destructiveHint: true },
  { command: "controller.remove-layer", summary: "Remove Animator Layer", destructiveHint: true },
  { command: "graphics.bake-start", summary: "Start Light Bake", destructiveHint: true },
  { command: "graphics.bake-clear", summary: "Clear Baked Lighting Data", destructiveHint: true },
  { command: "graphics.bake-reflection-probe", summary: "Bake Reflection Probe", destructiveHint: true },
  { command: "package.embed", summary: "Embed Package", destructiveHint: true, supportsDryRun: true },
  { command: "ui.create-panel-settings", summary: "Create PanelSettings", destructiveHint: true },
  { command: "ui.document.update", summary: "Update UI Document", destructiveHint: true, supportsDryRun: true },
  { command: "ui.document.delete", summary: "Delete UI Document", destructiveHint: true, supportsDryRun: true },
  { command: "ui.update-panel-settings", summary: "Update PanelSettings", destructiveHint: true, supportsDryRun: true },
  { command: "ui.link-stylesheet", summary: "Link Stylesheet", destructiveHint: true, supportsDryRun: true },
  { command: "ui.render-preview", summary: "Render UI Preview", destructiveHint: true, supportsDryRun: true }
];

const BY_COMMAND = new Map(SECURITY_POLICY.map((entry) => [entry.command, entry]));

export function securityPolicyForCommand(command: string | undefined): SecurityPolicyEntry | undefined {
  return command ? BY_COMMAND.get(command) : undefined;
}

export function destructiveCommands(): SecurityPolicyEntry[] {
  return [...SECURITY_POLICY];
}
