import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";

const commands = {
  discover: "unitycli init discover",
  packageInstall: "unitycli init package --project <UnityProject>",
  packageCheck: "unitycli init package --project <UnityProject> --check",
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
    protocolVersion: 1,
    aiInstructions: [
      "Run `unitycli init discover` from the user's current working directory before making installation decisions.",
      "Use discovery.requiredDecisions to ask the user which discovered Unity projects should receive the Bridge and which single Skill agent/scope/workspace target should be configured; do not infer or silently choose these values.",
      "For multiple selected Unity projects, run one `init package` call at a time in strict serial order. Wait for and verify each terminal result before submitting the next call.",
      "Install the UnityCLI Skill only once at the user-selected `.agents` or Claude workspace/user target unless the user explicitly requests more targets.",
      "After every successful package or Skill installation, run the corresponding command with `--check` and verify its business status.",
      "Stop immediately after the first unexpected CLI failure or failed business check. Report that terminal result and do not retry, change parameters, or continue with later installation effects.",
      "Do not start, close, restart, or terminate Unity Editor as part of installation. Package import occurs when the user next opens or refreshes the selected project.",
      "CLI installation calls are the authorized effects after the user answers the required decisions; do not add another confirmation gate.",
    ],
    commands,
    nextCommand: commands.discover,
    nextActions: [commands.discover],
  });
}
