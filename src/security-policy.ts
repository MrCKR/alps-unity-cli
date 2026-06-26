export interface SecurityPolicyEntry {
  command: string;
  title: string;
  destructiveHint: boolean;
  supportsDryRun?: boolean;
}

const SECURITY_POLICY: SecurityPolicyEntry[] = [
  { command: "asset.delete", title: "Delete Asset", destructiveHint: true, supportsDryRun: true },
  { command: "texture.generate", title: "Generate Texture", destructiveHint: true, supportsDryRun: true },
  { command: "script.update-or-create", title: "Update or Create Script", destructiveHint: true, supportsDryRun: true },
  { command: "scene.save", title: "Save Scene", destructiveHint: true, supportsDryRun: true },
  { command: "code.exec", title: "Execute Live Code", destructiveHint: true, supportsDryRun: true },
  { command: "object.modify", title: "Modify Object", destructiveHint: true, supportsDryRun: true },
  { command: "reflection.method.call", title: "Call Method via Reflection", destructiveHint: true },
  { command: "probuilder.delete-faces", title: "ProBuilder Delete Faces", destructiveHint: true },
  { command: "probuilder.detach-faces", title: "ProBuilder Detach Faces", destructiveHint: true },
  { command: "probuilder.combine-meshes", title: "ProBuilder Combine Meshes", destructiveHint: true },
  { command: "probuilder.merge-objects", title: "ProBuilder Merge Objects", destructiveHint: true },
  { command: "probuilder.flip-normals", title: "ProBuilder Flip Normals", destructiveHint: true },
  { command: "cinemachine.extension", title: "Cinemachine Extension Remove", destructiveHint: true },
  { command: "urp.feature", title: "URP Renderer Feature Modify", destructiveHint: true },
  { command: "editor.menu", title: "Execute Editor Menu", destructiveHint: true },
  { command: "script.edit", title: "Edit Script", destructiveHint: true },
  { command: "script.apply-edits", title: "Apply Script Edits", destructiveHint: true },
  { command: "package.remove", title: "Package Remove", destructiveHint: true },
  { command: "batch.run", title: "Batch Run", destructiveHint: true, supportsDryRun: true },
  { command: "instances.prune", title: "Prune Unity Instances", destructiveHint: true },
  { command: "jobs.cancel", title: "Cancel Job", destructiveHint: true },
  { command: "controller.remove-layer", title: "Remove Animator Layer", destructiveHint: true },
  { command: "graphics.bake-start", title: "Start Light Bake", destructiveHint: true },
  { command: "graphics.bake-clear", title: "Clear Baked Lighting Data", destructiveHint: true },
  { command: "graphics.bake-reflection-probe", title: "Bake Reflection Probe", destructiveHint: true },
  { command: "package.embed", title: "Embed Package", destructiveHint: true, supportsDryRun: true },
  { command: "ui.create-panel-settings", title: "Create PanelSettings", destructiveHint: true }
];

const BY_COMMAND = new Map(SECURITY_POLICY.map((entry) => [entry.command, entry]));

export function securityPolicyForCommand(command: string | undefined): SecurityPolicyEntry | undefined {
  return command ? BY_COMMAND.get(command) : undefined;
}

export function destructiveCommands(): SecurityPolicyEntry[] {
  return [...SECURITY_POLICY];
}
