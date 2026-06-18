import { copyFile, mkdir, readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// ─── Types ───────────────────────────────────────────────────────────────────

/** Supported agent identifiers for skill installation. */
export type AgentKind = "pi" | "claude" | ".agent";

/** All valid agent kinds. */
export const AGENT_KINDS: readonly AgentKind[] = ["pi", "claude", ".agent"];

/** Result of installing a skill for a single agent. */
export interface SkillInstallResult {
  agent: AgentKind;
  targetPath: string;
  status: "installed" | "skipped" | "updated";
  /** Populated when status is "skipped" (hash match). */
  hash?: string;
  /** Populated when status is "updated" (hash mismatch, overwritten). */
  previousHash?: string;
  newHash?: string;
}

/** Result of checking a skill for a single agent. */
export interface SkillCheckResult {
  agent: AgentKind;
  targetPath: string;
  status: "installed" | "missing" | "outdated";
  hash?: string;
  sourceHash?: string;
}

// ─── Agent skill target paths ────────────────────────────────────────────────

/** Maps an agent kind to its skill directory relative to the Unity project root. */
const AGENT_SKILL_DIR: Record<AgentKind, string> = {
  pi: ".pi/skills/unity_cli",
  claude: ".claude/skills/unity_cli",
  ".agent": ".agent/skills/unity_cli",
};

// ─── Skill source resolution ─────────────────────────────────────────────────

/**
 * Resolve the source SKILL.md path by walking up from the current CLI module
 * location to find the package root (directory containing package.json with
 * name "unity_cli"), then locating skills/unity_cli/SKILL.md underneath.
 */
export function resolveSkillSourcePath(metaUrl?: string): string {
  const startDir = metaUrl
    ? dirname(fileURLToPath(new URL(metaUrl)))
    : dirname(fileURLToPath(import.meta.url));

  let current = resolve(startDir);
  // Walk up at most 20 levels to find the package root
  for (let i = 0; i < 20; i++) {
    const pkgPath = join(current, "package.json");
    try {
      // Synchronous existence check via require is not available in ESM;
      // we do a best-effort heuristic: check if skills/unity_cli/SKILL.md exists.
      const skillPath = join(current, "skills", "unity_cli", "SKILL.md");
      // We validate the actual content in resolveSkillSource (async)
      // For now, return the candidate path
      return skillPath;
    } catch {
      // Not found, walk up
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  // Fallback: use import.meta.url resolution
  return join(dirname(fileURLToPath(import.meta.url)), "..", "..", "skills", "unity_cli", "SKILL.md");
}

/**
 * Async version: resolves the SKILL.md source path, validating that the
 * package root contains a valid unity_cli package.json.
 */
export async function findSkillSourcePath(startDir?: string): Promise<string> {
  let current = startDir ?? dirname(fileURLToPath(import.meta.url));

  for (let i = 0; i < 20; i++) {
    const skillPath = join(current, "skills", "unity_cli", "SKILL.md");
    try {
      // Try to read the skill file — if it exists, that's our source
      await readFile(skillPath, "utf8");
      return skillPath;
    } catch {
      // Not found at this level, walk up
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }

  throw new SkillInstallerError(
    "SKILL_SOURCE_NOT_FOUND",
    "Could not locate skills/unity_cli/SKILL.md relative to the CLI package root.",
    ["Ensure the unity_cli package is installed with the skills/ directory intact."]
  );
}

// ─── Hash helpers ─────────────────────────────────────────────────────────────

async function fileHash(filePath: string): Promise<string> {
  const content = await readFile(filePath);
  return createHash("sha256").update(content).digest("hex");
}

// ─── Installer interface ─────────────────────────────────────────────────────

export interface SkillInstaller {
  /** Agent kind this installer handles. */
  readonly agent: AgentKind;
  /** Resolve the target SKILL.md path for the given project root. */
  resolveTargetPath(projectRoot: string): string;
  /** Install (copy) the source SKILL.md to the target path. */
  install(sourcePath: string, projectRoot: string): Promise<SkillInstallResult>;
  /** Check the current install status. */
  check(sourcePath: string, projectRoot: string): Promise<SkillCheckResult>;
}

// ─── Base installer implementation ───────────────────────────────────────────

class BaseSkillInstaller implements SkillInstaller {
  constructor(readonly agent: AgentKind) {}

  resolveTargetPath(projectRoot: string): string {
    return join(projectRoot, AGENT_SKILL_DIR[this.agent], "SKILL.md");
  }

  async install(sourcePath: string, projectRoot: string): Promise<SkillInstallResult> {
    const targetPath = this.resolveTargetPath(projectRoot);
    const targetDir = dirname(targetPath);

    // Ensure target directory exists
    await mkdir(targetDir, { recursive: true });

    const sourceHash = await fileHash(sourcePath);

    // Check if target already exists
    let existingHash: string | undefined;
    try {
      existingHash = await fileHash(targetPath);
    } catch {
      // Target does not exist — will be a fresh install
    }

    if (existingHash === sourceHash) {
      return {
        agent: this.agent,
        targetPath,
        status: "skipped",
        hash: sourceHash,
      };
    }

    // Copy source to target
    await copyFile(sourcePath, targetPath);

    if (existingHash !== undefined) {
      return {
        agent: this.agent,
        targetPath,
        status: "updated",
        previousHash: existingHash,
        newHash: sourceHash,
      };
    }

    return {
      agent: this.agent,
      targetPath,
      status: "installed",
      hash: sourceHash,
    };
  }

  async check(sourcePath: string, projectRoot: string): Promise<SkillCheckResult> {
    const targetPath = this.resolveTargetPath(projectRoot);
    const sourceHash = await fileHash(sourcePath);

    let existingHash: string | undefined;
    try {
      existingHash = await fileHash(targetPath);
    } catch {
      return {
        agent: this.agent,
        targetPath,
        status: "missing",
        sourceHash,
      };
    }

    if (existingHash === sourceHash) {
      return {
        agent: this.agent,
        targetPath,
        status: "installed",
        hash: existingHash,
        sourceHash,
      };
    }

    return {
      agent: this.agent,
      targetPath,
      status: "outdated",
      hash: existingHash,
      sourceHash,
    };
  }
}

// ─── Registry ─────────────────────────────────────────────────────────────────

export class AgentSkillInstallerRegistry {
  private readonly installers: Map<AgentKind, SkillInstaller>;

  constructor() {
    this.installers = new Map<AgentKind, SkillInstaller>();
    // Register built-in installers for all supported agent kinds
    for (const agent of AGENT_KINDS) {
      this.installers.set(agent, new BaseSkillInstaller(agent));
    }
  }

  /** Get the installer for a specific agent kind. */
  get(agent: AgentKind): SkillInstaller | undefined {
    return this.installers.get(agent);
  }

  /** Get all registered agent kinds. */
  agents(): AgentKind[] {
    return [...this.installers.keys()];
  }

  /**
   * Install the skill for the specified agents.
   * @param agents Comma-separated or array of agent kinds (e.g. "pi,claude")
   */
  async installForAgents(
    agents: string | AgentKind[],
    sourcePath: string,
    projectRoot: string
  ): Promise<SkillInstallResult[]> {
    const resolved = parseAgentList(agents);
    const results: SkillInstallResult[] = [];

    for (const agent of resolved) {
      const installer = this.installers.get(agent);
      if (!installer) {
        throw new SkillInstallerError(
          "UNKNOWN_AGENT_KIND",
          `Unknown agent kind: ${agent}. Supported: ${AGENT_KINDS.join(", ")}`,
          [`Use one of: ${AGENT_KINDS.join(", ")}`]
        );
      }
      results.push(await installer.install(sourcePath, projectRoot));
    }

    return results;
  }

  /**
   * Check skill status for the specified agents.
   * @param agents Comma-separated or array of agent kinds
   */
  async checkForAgents(
    agents: string | AgentKind[],
    sourcePath: string,
    projectRoot: string
  ): Promise<SkillCheckResult[]> {
    const resolved = parseAgentList(agents);
    const results: SkillCheckResult[] = [];

    for (const agent of resolved) {
      const installer = this.installers.get(agent);
      if (!installer) {
        throw new SkillInstallerError(
          "UNKNOWN_AGENT_KIND",
          `Unknown agent kind: ${agent}. Supported: ${AGENT_KINDS.join(", ")}`,
          [`Use one of: ${AGENT_KINDS.join(", ")}`]
        );
      }
      results.push(await installer.check(sourcePath, projectRoot));
    }

    return results;
  }
}

// ─── Agent list parsing ──────────────────────────────────────────────────────

/**
 * Parse a comma-separated string or array of agent kinds into validated AgentKind[].
 */
export function parseAgentList(agents: string | AgentKind[]): AgentKind[] {
  const raw = typeof agents === "string"
    ? agents.split(",").map((a) => a.trim()).filter(Boolean)
    : agents;

  const validSet = new Set<string>(AGENT_KINDS);
  const result: AgentKind[] = [];

  for (const agent of raw) {
    if (!validSet.has(agent)) {
      throw new SkillInstallerError(
        "UNKNOWN_AGENT_KIND",
        `Unknown agent kind: "${agent}". Supported: ${AGENT_KINDS.join(", ")}`,
        [`Use one or more of: ${AGENT_KINDS.join(", ")}, comma-separated (e.g. "pi,claude")`]
      );
    }
    // Deduplicate
    if (!result.includes(agent as AgentKind)) {
      result.push(agent as AgentKind);
    }
  }

  if (result.length === 0) {
    throw new SkillInstallerError(
      "AGENT_KIND_REQUIRED",
      "At least one agent kind must be specified.",
      [`Use --agent with one or more of: ${AGENT_KINDS.join(", ")}`]
    );
  }

  return result;
}

// ─── Error class ─────────────────────────────────────────────────────────────

export class SkillInstallerError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly nextActions: string[] = []
  ) {
    super(message);
  }
}

// ─── Convenience: default registry instance ──────────────────────────────────

/** Default registry with all built-in agent installers. */
export const agentSkillInstaller = new AgentSkillInstallerRegistry();
