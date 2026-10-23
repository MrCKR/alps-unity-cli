import { failure, success } from "../output.js";
import { cliVersion } from "../cli-version.js";
import type { CliResult } from "../types.js";

const commands = {
  discover: "unitycli init discover",
  packageInstall: "unitycli init package --project <UnityProject>",
  packageInstallAndCloseEditor: "unitycli init package --project <UnityProject> --close-editor",
  packageCheck: "unitycli init package --project <UnityProject> --check",
  explicitSkillInstall: "unitycli init skill --target <SkillDirectory>",
  explicitSkillCheck: "unitycli init skill --target <SkillDirectory> --check",
  workspaceSkillInstall: "unitycli init skill --agent <.agents|claude> --scope workspace --workspace <path>",
  workspaceSkillCheck: "unitycli init skill --agent <.agents|claude> --scope workspace --workspace <path> --check",
  userSkillInstall: "unitycli init skill --agent <.agents|claude> --scope user",
  userSkillCheck: "unitycli init skill --agent <.agents|claude> --scope user --check",
} as const;

/** 返回静态、自足、无现场读取的 AI 安装协议。 */
export async function handleInitHelp(args: string[], flags: Record<string, string | boolean>, requestId: string): Promise<CliResult> {
  if (args.length) return failure(requestId, "E_INIT_ARGUMENT_UNEXPECTED", "init help does not accept positional arguments.", ["Run `unitycli init help` without additional arguments."]);
  const unknown = Object.keys(flags).find((name) => name !== "pretty");
  if (unknown) return failure(requestId, "E_SHELL_OPTION_UNKNOWN", `Unknown option for init help: --${unknown}.`, ["Run `unitycli init help` without options."], { option: `--${unknown}` });
  return success(requestId, {
    action: "init_help",
    audience: "ai",
    protocolVersion: 3,
    version: await cliVersion(),
    aiInstructions: [
      "Run `unitycli init discover` from the user's current working directory and use its compact current-project plan.",
      "Treat every project, scope, host, or Skill directory the user already specified as final; never ask them to choose it again. Use `init skill --target` for an explicit directory such as `.pi/skills/unitycli`.",
      "When discovery returns one current or ancestor Unity project, use it without another project-selection question. Ask only entries present in discovery.requiredDecisions.",
      "An open Unity Editor is a normal installation state, not a blocking condition. Run `init package --project <UnityProject>` with the Editor open and running: the installer replaces the package in place and never closes, starts, or restarts Unity for you.",
      "Only when the publish reports `PACKAGE_DIRECTORY_LOCKED` does closing the Editor matter. Report the locked package directory, state that the previous package state was restored, ask once whether this exact Unity project may be closed normally, and rerun the same command with `--close-editor` only after approval. Never force-terminate Unity.",
      "After a successful install into a project whose Editor is running, run `unitycli tools run editor.refresh --instance <projectPath|name|alias>` so the Editor imports the replaced package, then run `init package --check` to verify the installed bytes. A refresh result with `changed:false` means the Editor had nothing left to import and is still a successful step; judge the installation by the install readback and by the check's `cli.version`, `cli.compatibilityRange`, `installedVersion`, and `availableVersion`, not by whether a refresh reported an import.",
      "Bridge and Skill installation are independent authorized effects. A failure blocks only that effect and its check; continue other user-authorized independent installation effects and report every final status.",
      "Run one corresponding `--check` after each successful package or Skill installation and verify its business status.",
      "For a recoverable installation error, follow its exact nextActions after the required external state changes. Do not create unbounded retries or change the user's selected targets.",
      "A pre-effect package precheck block means no package file was written: `PACKAGE_ASSEMBLY_CONFLICT` reports copies of a protected dependency that the Bridge must own inside its own package, so report the exact paths and identities from details.conflicts, let the user decide which copy to remove or relocate, and never delete, move, or rename project files yourself; `PACKAGE_ASSEMBLY_SCAN_BUDGET_EXHAUSTED` is a scan-scale backstop, not a conflict, so report its counts instead of changing the project.",
      "The user's installation request authorizes the selected package and Skill effects. Installing while Unity is open needs no confirmation; closing an Editor is the only separate user decision, and it is required solely after a reported directory lock.",
    ],
    commands,
    nextCommand: commands.discover,
    nextActions: [commands.discover],
  });
}
