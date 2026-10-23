import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { FACT_ADDITIONS, RETIRED_CODES, SEMANTIC_OVERRIDES, SYNONYM_TARGETS } from "./error-code-renames.mjs";

/** 事实词表（闭集，按长度优先匹配）；末位 token 序列命中即视为该事实。 */
const BASE_FACTS = [
  ["BUDGET_EXHAUSTED", "BUDGET", "EXHAUSTED"],
  ["LIMIT_EXCEEDED", "LIMIT", "EXCEEDED"],
  ["NOT_FOUND", "NOT", "FOUND"],
  ["NOT_OBSERVED", "NOT", "OBSERVED"],
  ["OUT_OF_RANGE", "OUT", "OF", "RANGE"],
  ["SOURCE_EQUALS_DESTINATION", "SOURCE", "EQUALS", "DESTINATION"],
  ["ELEMENT_CAPTURE_BOUNDS", "ELEMENT", "CAPTURE", "BOUNDS"],
  ["ELEMENT_SELECTOR_BOUNDS", "ELEMENT", "SELECTOR", "BOUNDS"],
  ["NOT_A_BLEND_TREE", "NOT", "A", "BLEND", "TREE"],
  ["NOT_PREFAB_INSTANCE", "NOT", "PREFAB", "INSTANCE"],
  ["REQUIRES_INVOCATION", "REQUIRES", "INVOCATION"],
  ["REQUIRES_EDIT_MODE", "REQUIRES", "EDIT", "MODE"],
  ["REQUIRES_PAUSE", "REQUIRES", "PAUSE"],
  ["REQUIRES_STOP", "REQUIRES", "STOP"],
  ["ALREADY_CONSUMED", "ALREADY", "CONSUMED"],
  ["ALREADY_SATISFIED", "ALREADY", "SATISFIED"],
  ["UNKNOWN_INSTALLED", "UNKNOWN", "INSTALLED"],
  ["SAVE_ALL_UNTITLED", "SAVE", "ALL", "UNTITLED"],
  ["VIEW_NOT_AVAILABLE", "VIEW", "NOT", "AVAILABLE"],
  ["PENDING_SCAN_LIMIT", "PENDING", "SCAN", "LIMIT"],
  ["QUERY_TOO_LONG", "QUERY", "TOO", "LONG"],
  ["GO_PARENT_CYCLE", "GO", "PARENT", "CYCLE"],
  ["UNPROVEN", "UNPROVEN"],
  ["ABORTED", "ABORTED"],
  ["OBSOLETE", "OBSOLETE"],
  ["UNBOUNDED", "UNBOUNDED"],
  ["NOT_OWNED", "NOT", "OWNED"],
  ["COMPILING", "COMPILING"],
  ["IMPORTING", "IMPORTING"],
  ["TRANSITIONING", "TRANSITIONING"],
  ["QUITTING", "QUITTING"],
  ["PLAY_MODE", "PLAY", "MODE"],
  ["DOMAIN_RELOAD", "DOMAIN", "RELOAD"],
  ["UNSUPPORTED", "UNSUPPORTED"],
  ["UNAVAILABLE", "UNAVAILABLE"],
  ["UNPROVABLE", "UNPROVABLE"],
  ["UNVERIFIABLE", "UNVERIFIABLE"],
  ["UNOBSERVABLE", "UNOBSERVABLE"],
  ["UNRESPONSIVE", "UNRESPONSIVE"],
  ["INTERNAL_ERROR", "INTERNAL", "ERROR"],
  ["IO_ERROR", "IO", "ERROR"],
  ["OBSERVER_ASYNC", "OBSERVER", "ASYNC"],
  ["NOT_PREPARED", "NOT", "PREPARED"],
  ["NOT_INSTALLED", "NOT", "INSTALLED"],
  ["NOT_REARMED", "NOT", "REARMED"],
  ["OWNED_BY_PROGRAM", "OWNED", "BY", "PROGRAM"],
  ["NO_CONTROLLER", "NO", "CONTROLLER"],
  ["NO_FIELDS", "NO", "FIELDS"],
  ["NO_DATA", "NO", "DATA"],
  ["UNREADABLE", "UNREADABLE"],
  ["SOURCE_UNREADABLE", "SOURCE", "UNREADABLE"],
  ["WRONG_STATE", "WRONG", "STATE"],
  ["FORCE_KINEMATIC", "FORCE", "KINEMATIC"],
  ["OVERLAPS_SOURCE", "OVERLAPS", "SOURCE"],
  ["UNBOUND", "UNBOUND"],
  ["UNEXPECTED", "UNEXPECTED"],
  ["IMMUTABLE", "IMMUTABLE"],
  ["SUPPRESSED", "SUPPRESSED"],
  ["INTERRUPTED", "INTERRUPTED"],
  ["INCOMPLETE", "INCOMPLETE"],
  ["DUPLICATE", "DUPLICATE"],
  ["OCCUPIED", "OCCUPIED"],
  ["UNHEALTHY", "UNHEALTHY"],
  ["FORBIDDEN", "FORBIDDEN"],
  ["MISMATCH", "MISMATCH"],
  ["AMBIGUOUS", "AMBIGUOUS"],
  ["CONFLICT", "CONFLICT"],
  ["CANCELLED", "CANCELLED"],
  ["EXPIRED", "EXPIRED"],
  ["PENDING", "PENDING"],
  ["CHANGED", "CHANGED"],
  ["BLOCKED", "BLOCKED"],
  ["REQUIRED", "REQUIRED"],
  ["EXISTS", "EXISTS"],
  ["REUSED", "REUSED"],
  ["REJECTED", "REJECTED"],
  ["STOPPED", "STOPPED"],
  ["REPLACED", "REPLACED"],
  ["RECURSION", "RECURSION"],
  ["OVERRUN", "OVERRUN"],
  ["SKIPPED", "SKIPPED"],
  ["TOO_LARGE", "TOO", "LARGE"],
  ["EXCEPTION", "EXCEPTION"],
  ["DEADLINE", "DEADLINE"],
  ["PROTOCOL", "PROTOCOL"],
  ["NOT_PREFAB", "NOT", "PREFAB"],
  ["UNSAFE", "UNSAFE"],
  ["STALE", "STALE"],
  ["TIMEOUT", "TIMEOUT"],
  ["INVALID", "INVALID"],
  ["FAILED", "FAILED"],
  ["UNKNOWN", "UNKNOWN"],
  ["MISSING", "MISSING"],
  ["ACTIVE", "ACTIVE"],
  ["EMPTY", "EMPTY"],
  ["OPEN", "OPEN"],
  ["RUNNING", "RUNNING"],
  ["PAUSED", "PAUSED"],
  ["FULL", "FULL"],
  ["NOOP", "NOOP"],
  ["CYCLE", "CYCLE"],
  ["DIRTY", "DIRTY"],
  ["RISK", "RISK"],
  ["NULL", "NULL"],
  ["BUSY", "BUSY"],
  ["RANGE", "RANGE"],
  ["OBSERVED", "OBSERVED"],
  ["WRITABLE", "WRITABLE"],
  ["NESTED", "NESTED"],
  ["UNDEFINED", "UNDEFINED"]
];

/** 事实 → 默认语义类。 */
export const FACT_SEMANTICS = {
  INVALID: ["false", false], REQUIRED: ["false", false], UNSUPPORTED: ["false", false],
  UNAVAILABLE: ["false", false], NOT_FOUND: ["false", false], EXISTS: ["false", false],
  CONFLICT: ["false", false], REUSED: ["false", false], AMBIGUOUS: ["false", false],
  EMPTY: ["false", false], OUT_OF_RANGE: ["false", false], RANGE: ["false", false],
  MISSING: ["false", false], NOT_OBSERVED: ["unknown", false], OBSERVED: ["unknown", false],
  UNPROVEN: ["unknown", false], UNPROVABLE: ["unknown", false], MISMATCH: ["true", false],
  CHANGED: ["true", false], READONLY: ["false", false], WRITABLE: ["false", false],
  STALE: ["false", true], BUSY: ["false", true], RUNNING: ["false", true],
  ACTIVE: ["false", false], OPEN: ["false", false], PENDING: ["true", true],
  CANCELLED: ["true", false], BLOCKED: ["false", false], EXPIRED: ["false", false], DISABLED: ["false", false],
  LIMIT_EXCEEDED: ["false", false], BUDGET_EXHAUSTED: ["false", false],
  TIMEOUT: ["unknown", false], FAILED: ["unknown", false], UNKNOWN: ["unknown", false],
  ALREADY_CONSUMED: ["false", false], ALREADY_SATISFIED: ["false", false],
  NO_CONTROLLER: ["false", false], UNOBSERVABLE: ["unknown", false],
  NOT_PREFAB: ["false", false], SOURCE_EQUALS_DESTINATION: ["false", false],
  INTERNAL_ERROR: ["unknown", false], IO_ERROR: ["unknown", false], PROTOCOL: ["false", false], SKIPPED: ["false", false],
  TOO_LARGE: ["false", false], EXCEPTION: ["unknown", false], INCOMPLETE: ["unknown", false],
  OBSERVER_ASYNC: ["false", false], DUPLICATE: ["false", false], REJECTED: ["false", false],
  NESTED: ["false", false], PAUSED: ["false", true], REQUIRES_INVOCATION: ["false", false],
  NOT_PREPARED: ["false", false], INTERRUPTED: ["unknown", false], STOPPED: ["true", false],
  UNBOUND: ["false", false], UNREADABLE: ["false", false], SOURCE_UNREADABLE: ["false", false],
  WRONG_STATE: ["false", false], RECURSION: ["false", false], REPLACED: ["false", false],
  UNVERIFIABLE: ["unknown", false], NOT_INSTALLED: ["false", false], DEADLINE: ["unknown", false],
  REQUIRES_PAUSE: ["false", false], REQUIRES_EDIT_MODE: ["false", false], REQUIRES_STOP: ["false", false],
  CYCLE: ["false", false], OVERRUN: ["unknown", false], NOT_REARMED: ["false", false],
  OWNED_BY_PROGRAM: ["false", false], SUPPRESSED: ["false", true], RISK: ["false", false],
  UNEXPECTED: ["false", false], FORBIDDEN: ["false", false], IMMUTABLE: ["false", false],
  OCCUPIED: ["false", true], UNRESPONSIVE: ["unknown", true], DIRTY: ["false", false],
  UNHEALTHY: ["unknown", true], UNSAFE: ["false", false], OVERLAPS_SOURCE: ["false", false],
  NULL: ["false", false], NOT_A_BLEND_TREE: ["false", false], NOT_PREFAB_INSTANCE: ["false", false],
  UNKNOWN_INSTALLED: ["false", false], QUERY_TOO_LONG: ["false", false],
  SAVE_ALL_UNTITLED: ["false", false], UNLOAD_LAST: ["false", false],
  VIEW_NOT_AVAILABLE: ["false", false], PENDING_SCAN_LIMIT: ["false", false],
  FORCE_KINEMATIC: ["false", false], NO_DATA: ["false", false], NO_FIELDS: ["false", false],
  GO_PARENT_CYCLE: ["false", false], ELEMENT_CAPTURE_BOUNDS: ["false", false],
  ELEMENT_SELECTOR_BOUNDS: ["false", false], PANEL_BOUNDS: ["false", false],
  FULL: ["false", true], NOOP: ["true", false], UNDEFINED: ["false", false],
  ABORTED: ["true", false], UNBOUNDED: ["false", false], NOT_OWNED: ["false", false],
  OBSOLETE: ["false", false], COMPILING: ["false", true], IMPORTING: ["false", true],
  TRANSITIONING: ["false", true], QUITTING: ["false", true], PLAY_MODE: ["false", true],
  DOMAIN_RELOAD: ["false", true]
};

const EXTRA_FACTS = FACT_ADDITIONS.map(([fact]) => [fact, ...fact.split("_")]);
export const FACT_TOKENS = [...BASE_FACTS, ...EXTRA_FACTS];

const SKIP = /(\\|\/)(node_modules|dist|\.git|\.issues|\.plan|Library|Temp)(\\|\/)/;
const PRODUCT_SOURCE = ["unitypkg", "extensions", "src"];

function walk(dir, out = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (SKIP.test(path)) continue;
    if (entry.isDirectory()) walk(path, out);
    else if (/\.(cs|ts)$/.test(path) && !/\.g\.cs$/.test(path) && !/\.generated-/.test(path) && !/\/Tests?\//.test(path.split(sep).join("/"))) out.push(path);
  }
  return out;
}

export function scanErrorCodeUsages(root) {
  const files = PRODUCT_SOURCE.flatMap((dir) => (existsSync(join(root, dir)) ? walk(join(root, dir)) : []));
  const dynamicConfigPath = join(root, "contracts", "error-code-dynamic.json");
  const dynamicConfig = existsSync(dynamicConfigPath) ? JSON.parse(readFileSync(dynamicConfigPath, "utf8")) : { entries: [] };
  const prefixes = new Set(dynamicConfig.entries.map((entry) => entry.prefix));
  const codes = new Map();
  const dynamicSites = [];
  for (const file of files) {
    const rel = relative(root, file).split(sep).join("/");
    const text = readFileSync(file, "utf8");
    for (const match of text.matchAll(/"E_[A-Z0-9_]+"/g)) {
      const code = match[0].slice(1, -1);
      if (prefixes.has(code)) continue;
      if (!codes.has(code)) codes.set(code, { code, files: new Set() });
      codes.get(code).files.add(rel);
    }
    for (const match of text.matchAll(/\+\s*errorPrefix\s*\+\s*"(_[A-Z0-9_]+)"/g)) dynamicSites.push({ file: rel, suffix: match[1] });
  }
  // 已知运行时拼接码由显式清单枚举，使它们可登记、可文档化、不复存在不可见码。
  for (const entry of dynamicConfig.entries) {
    for (const suffix of entry.suffixes) {
      const code = `${entry.prefix}${suffix}`;
      if (!codes.has(code)) codes.set(code, { code, files: new Set() });
      codes.get(code).files.add(entry.file);
    }
  }
  return { files, codes, dynamicSites, dynamicConfig };
}

/** 解析事实：末位 token 序列命中闭集词表。 */
export function classifyCode(tokens) {
  for (const [fact, ...factTokens] of FACT_TOKENS) {
    if (tokens.length <= factTokens.length) continue;
    if (tokens.slice(tokens.length - factTokens.length).join("_") !== factTokens.join("_")) continue;
    return { fact, subjectTokens: tokens.slice(0, tokens.length - factTokens.length) };
  }
  return { fact: null, subjectTokens: tokens };
}

/** 事实默认语义 + 站点 override（override 必须带理由）。 */
function semanticsFor(code, fact) {
  const base = FACT_SEMANTICS[fact] ?? ["unknown", false];
  const override = SEMANTIC_OVERRIDES[code];
  return override
    ? { effectStarted: override.effectStarted ?? base[0], transient: override.transient ?? base[1], reason: override.reason }
    : { effectStarted: base[0], transient: base[1], reason: null };
}

/** 一条码的分类结果：目标名、事实、语义与原因。 */
export function classifyOne(code) {
  const tokens = code.replace(/^E_/, "").split("_").filter(Boolean);
  const parsed = classifyCode(tokens);
  if (parsed.fact) {
    const semantics = semanticsFor(code, parsed.fact);
    return { code, target: code, fact: parsed.fact, effectStarted: semantics.effectStarted, transient: semantics.transient, semanticsReason: semantics.reason, status: "conforming", reason: "fact-closed" };
  }
  const last = tokens[tokens.length - 1];
  const lastTwo = tokens.slice(-2).join("_");
  if (lastTwo === "TIMED_OUT") {
    const target = ["E", ...tokens.slice(0, -2), "TIMEOUT"].join("_");
    return { code, target, fact: "TIMEOUT", effectStarted: "unknown", transient: false, status: "rename", reason: "synonym" };
  }
  if (SYNONYM_TARGETS[last]) {
    const target = ["E", ...tokens.slice(0, -1), SYNONYM_TARGETS[last]].join("_");
    const targetTokens = target.replace(/^E_/, "").split("_").filter(Boolean);
    const parsedTarget = classifyCode(targetTokens);
    const semantics = parsedTarget.fact ? semanticsFor(code, parsedTarget.fact) : null;
    return { code, target, fact: parsedTarget.fact, effectStarted: semantics?.effectStarted ?? "unknown", transient: semantics?.transient ?? false, semanticsReason: semantics?.reason ?? null, status: "rename", reason: "synonym" };
  }
  return { code, target: null, fact: null, effectStarted: "unknown", transient: false, status: "residual", reason: "fact-not-in-closed-set", tail: tokens.slice(-3).join("_") };
}

export function retiredCodes() {
  return RETIRED_CODES;
}

export function classifyAll(root) {
  const { codes, dynamicSites, files, dynamicConfig } = scanErrorCodeUsages(root);
  const entries = [...codes.values()].map(({ code, files: emitters }) => ({ ...classifyOne(code), files: [...emitters] }));
  entries.sort((left, right) => left.code.localeCompare(right.code));
  return { files, dynamicSites, dynamicConfig, entries };
}
