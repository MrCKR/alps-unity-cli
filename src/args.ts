export interface ParsedArgs {
  positional: string[];
  flags: Record<string, string | boolean>;
}

export function parseArgs(argv: string[]): ParsedArgs {
  const positional: string[] = [];
  const flags: Record<string, string | boolean> = {};

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      positional.push(token);
      continue;
    }

    const withoutPrefix = token.slice(2);
    const equalsIndex = withoutPrefix.indexOf("=");
    if (equalsIndex >= 0) {
      flags[withoutPrefix.slice(0, equalsIndex)] = withoutPrefix.slice(equalsIndex + 1);
      continue;
    }

    const next = argv[index + 1];
    if (next && !next.startsWith("--")) {
      const collected = collectMultitokenJsonFlag(argv, index + 1, withoutPrefix);
      flags[withoutPrefix] = collected.value;
      index = collected.nextIndex;
    } else {
      flags[withoutPrefix] = true;
    }
  }

  return { positional, flags };
}

/** 容错 PowerShell 将 --input JSON 按空格拆成多个 argv token 的常见情况。 */
function collectMultitokenJsonFlag(argv: string[], valueIndex: number, flagName: string): { value: string; nextIndex: number } {
  const first = argv[valueIndex];
  if (flagName !== "input" || !looksLikeJsonStart(first) || looksLikeJsonEnd(first)) {
    return { value: first, nextIndex: valueIndex };
  }

  const parts = [first];
  let index = valueIndex;
  for (let nextIndex = valueIndex + 1; nextIndex < argv.length; nextIndex += 1) {
    const token = argv[nextIndex];
    if (token.startsWith("--")) {
      break;
    }
    parts.push(token);
    index = nextIndex;
    if (looksLikeJsonEnd(token)) {
      break;
    }
  }

  return { value: parts.join(" "), nextIndex: index };
}

function looksLikeJsonStart(value: string): boolean {
  const trimmed = value.trimStart();
  return trimmed.startsWith("{") || trimmed.startsWith("[");
}

function looksLikeJsonEnd(value: string): boolean {
  const trimmed = value.trimEnd();
  return trimmed.endsWith("}") || trimmed.endsWith("\\}") || trimmed.endsWith("]") || trimmed.endsWith("\\]");
}

export function getString(flags: Record<string, string | boolean>, name: string): string | undefined {
  const value = flags[name];
  return typeof value === "string" ? value : undefined;
}

export function getBoolean(flags: Record<string, string | boolean>, name: string): boolean {
  return flags[name] === true || flags[name] === "true";
}

export function getNumber(flags: Record<string, string | boolean>, name: string): number | undefined {
  const value = getString(flags, name);
  if (!value) {
    return undefined;
  }
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}
