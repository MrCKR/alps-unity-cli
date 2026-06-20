export interface SecurityPolicyEntry {
  command: string;
  title: string;
  destructiveHint: boolean;
  supportsDryRun?: boolean;
}

const SECURITY_POLICY: SecurityPolicyEntry[] = [
  { command: "asset.delete", title: "Delete Asset", destructiveHint: true, supportsDryRun: true },
  { command: "go.delete", title: "Delete GameObject", destructiveHint: true, supportsDryRun: true },
  { command: "scene.save", title: "Save Scene", destructiveHint: true, supportsDryRun: true },
  { command: "code.exec", title: "Execute Live Code", destructiveHint: true, supportsDryRun: true },
  { command: "component.remove", title: "Remove Component", destructiveHint: true },
  { command: "probuilder.csg", title: "ProBuilder CSG", destructiveHint: true },
  { command: "cinemachine.extension", title: "Cinemachine Extension Remove", destructiveHint: true },
  { command: "urp.feature", title: "URP Renderer Feature Modify", destructiveHint: true },
  { command: "editor.menu", title: "Execute Editor Menu", destructiveHint: true },
  { command: "script.edit", title: "Edit Script", destructiveHint: true },
  { command: "cli.package.remove", title: "Package Remove", destructiveHint: true },
  { command: "cli.batch.run", title: "Batch Run", destructiveHint: true, supportsDryRun: true },
  { command: "test.cancel", title: "Cancel Test Job", destructiveHint: true },
  { command: "build.cancel", title: "Cancel Build Job", destructiveHint: true }
];

const BY_COMMAND = new Map(SECURITY_POLICY.map((entry) => [entry.command, entry]));

export function securityPolicyForCommand(command: string | undefined): SecurityPolicyEntry | undefined {
  return command ? BY_COMMAND.get(command) : undefined;
}

export function destructiveCommands(): SecurityPolicyEntry[] {
  return [...SECURITY_POLICY];
}
