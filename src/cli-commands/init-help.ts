import { failure, success } from "../output.js";
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
export function handleInitHelp(args: string[], flags: Record<string, string | boolean>, requestId: string): CliResult {
  if (args.length) return failure(requestId, "E_INIT_ARGUMENT_UNEXPECTED", "init help does not accept positional arguments.", ["Run `unitycli init help` without additional arguments."]);
  const unknown = Object.keys(flags).find((name) => name !== "pretty");
  if (unknown) return failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for init help: --${unknown}.`, ["Run `unitycli init help` without options."], { option: `--${unknown}` });
  return success(requestId, {
    action: "init_help",
    audience: "ai",
    protocolVersion: 2,
    aiInstructions: [
      "Run `unitycli init discover` from the user's current working directory and use its compact current-project plan.",
      "Treat every project, scope, host, or Skill directory the user already specified as final; never ask them to choose it again. Use `init skill --target` for an explicit directory such as `.pi/skills/unitycli`.",
      "When discovery returns one current or ancestor Unity project, use it without another project-selection question. Ask only entries present in discovery.requiredDecisions.",
      "If the exact target Unity project is running, ask once whether it may be closed normally. After approval use `init package --close-editor`; never force-terminate Unity or decide an unsaved-scene prompt for the user.",
      "Bridge and Skill installation are independent authorized effects. A failure blocks only that effect and its check; continue other user-authorized independent installation effects and report every final status.",
      "Run one corresponding `--check` after each successful package or Skill installation and verify its business status.",
      "For a recoverable installation error, follow its exact nextActions after the required external state changes. Do not create unbounded retries or change the user's selected targets.",
      "Do not start or restart Unity Editor during installation. Normal close is allowed only for the exact project and only through the explicitly authorized --close-editor package call.",
      "The user's installation request authorizes the selected package and Skill effects; do not add confirmation gates except the separate close-Editor decision.",
    ],
    commands,
    nextCommand: commands.discover,
    nextActions: [commands.discover],
  });
}
