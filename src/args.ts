export type CliOptionArity = "boolean" | "value";

export interface CliArgsSpec {
  options: Readonly<Record<string, CliOptionArity>>;
  commandNames: ReadonlySet<string>;
  /** 该顶层命令后的 token 由目标能力 schema 二次解析。 */
  passthroughAfterCommand?: string;
}

export interface ArgParseIssue {
  code: "E_UNKNOWN_OPTION" | "E_OPTION_VALUE_REQUIRED" | "E_OPTION_VALUE_INVALID";
  option: string;
  expected?: CliOptionArity;
  actual?: string;
}

export interface ParsedArgs {
  positional: string[];
  flags: Record<string, string | boolean>;
  passthrough?: string[];
  issue?: ArgParseIssue;
}

/**
 * 按显式 arity 解析 CLI token。布尔开关绝不消费后续 token；带值 option 在命令前遇到顶层命令名时按缺值失败。
 * `--` 之后的 token 全部作为 positional，便于把以 `--` 开头的搜索词等业务值显式传入。
 */
export function parseArgs(argv: string[], spec: CliArgsSpec): ParsedArgs {
  const positional: string[] = [];
  const flags: Record<string, string | boolean> = {};
  let passthrough: string[] | undefined;
  let issue: ArgParseIssue | undefined;
  let optionsEnabled = true;

  for (let index = 0; index < argv.length; index += 1) {
    if (spec.passthroughAfterCommand && positional.length === 1 && positional[0] === spec.passthroughAfterCommand) {
      passthrough = argv.slice(index);
      break;
    }
    const token = argv[index];
    if (optionsEnabled && token === "--") {
      optionsEnabled = false;
      continue;
    }
    if (!optionsEnabled || !token.startsWith("--")) {
      positional.push(token);
      continue;
    }

    const withoutPrefix = token.slice(2);
    const equalsIndex = withoutPrefix.indexOf("=");
    const name = equalsIndex >= 0 ? withoutPrefix.slice(0, equalsIndex) : withoutPrefix;
    const attachedValue = equalsIndex >= 0 ? withoutPrefix.slice(equalsIndex + 1) : undefined;
    const arity = spec.options[name];
    if (!arity) {
      issue ??= { code: "E_UNKNOWN_OPTION", option: name || token };
      continue;
    }

    if (arity === "boolean") {
      if (attachedValue === undefined) {
        flags[name] = true;
      } else if (attachedValue === "true" || attachedValue === "false") {
        flags[name] = attachedValue;
      } else {
        issue ??= { code: "E_OPTION_VALUE_INVALID", option: name, expected: arity, actual: attachedValue };
      }
      continue;
    }

    if (attachedValue !== undefined) {
      if (attachedValue.length === 0) issue ??= { code: "E_OPTION_VALUE_REQUIRED", option: name, expected: arity };
      else flags[name] = attachedValue;
      continue;
    }

    const next = argv[index + 1];
    const wouldConsumeCommand = positional.length === 0 && next !== undefined && spec.commandNames.has(next);
    if (next === undefined || next.startsWith("--") || wouldConsumeCommand) {
      issue ??= { code: "E_OPTION_VALUE_REQUIRED", option: name, expected: arity, ...(next !== undefined ? { actual: next } : {}) };
      continue;
    }

    const collected = collectMultitokenJsonFlag(argv, index + 1, name);
    flags[name] = collected.value;
    index = collected.nextIndex;
  }

  return { positional, flags, ...(passthrough ? { passthrough } : {}), ...(issue ? { issue } : {}) };
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
    if (token.startsWith("--")) break;
    parts.push(token);
    index = nextIndex;
    if (looksLikeJsonEnd(token)) break;
  }

  return { value: parts.join(" "), nextIndex: index };
}

/** 判断多 token JSON 的起始边界。 */
function looksLikeJsonStart(value: string): boolean {
  const trimmed = value.trimStart();
  return trimmed.startsWith("{") || trimmed.startsWith("[");
}

/** 判断 PowerShell 传递的普通或转义 JSON 结束边界。 */
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
