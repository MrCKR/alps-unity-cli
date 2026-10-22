import assert from "node:assert/strict";
import test from "node:test";
import { parseArgs, type CliArgsSpec } from "../src/args.js";

const spec: CliArgsSpec = {
  commandNames: new Set(["tools", "command", "extensions", "init", "target", "instances", "doctor"]),
  passthroughAfterCommand: "command",
  options: {
    help: "boolean",
    instance: "value",
    input: "value",
    "dry-run": "boolean",
  },
};

test("boolean option 不消费后续命令 token", () => {
  assert.deepEqual(parseArgs(["--help", "tools", "group"], spec), {
    positional: ["tools", "group"],
    flags: { help: true },
  });
});

test("value option 支持命令前后与等号形式", () => {
  assert.deepEqual(parseArgs(["--instance", "editor-a", "tools", "group"], spec), {
    positional: ["tools", "group"],
    flags: { instance: "editor-a" },
  });
  assert.deepEqual(parseArgs(["tools", "group", "--instance=editor-a"], spec), {
    positional: ["tools", "group"],
    flags: { instance: "editor-a" },
  });
});

test("value option 不把顶层命令或下一个 option 当作值", () => {
  assert.deepEqual(parseArgs(["--instance", "tools", "group"], spec), {
    positional: ["tools", "group"],
    flags: {},
    issue: { code: "E_OPTION_VALUE_REQUIRED", option: "instance", expected: "value", actual: "tools" },
  });
  assert.deepEqual(parseArgs(["tools", "group", "--instance", "--dry-run"], spec), {
    positional: ["tools", "group"],
    flags: { "dry-run": true },
    issue: { code: "E_OPTION_VALUE_REQUIRED", option: "instance", expected: "value", actual: "--dry-run" },
  });
});

test("unknown 与非法 boolean attached value 返回首个结构化 issue", () => {
  assert.deepEqual(parseArgs(["--unknown", "tools", "group"], spec), {
    positional: ["tools", "group"],
    flags: {},
    issue: { code: "E_SHELL_OPTION_UNKNOWN", option: "unknown" },
  });
  assert.deepEqual(parseArgs(["tools", "group", "--help=maybe"], spec), {
    positional: ["tools", "group"],
    flags: {},
    issue: { code: "E_OPTION_VALUE_INVALID", option: "help", expected: "boolean", actual: "maybe" },
  });
});

test("-- 终止 option 解析", () => {
  assert.deepEqual(parseArgs(["tools", "search", "--", "--dry-run"], spec), {
    positional: ["tools", "search", "--dry-run"],
    flags: {},
  });
});

test("PowerShell 多 token input 保持一个 JSON 值", () => {
  assert.deepEqual(parseArgs(["tools", "run", "demo", "--input", "{\"name\":", "\"hello world\"}"], spec), {
    positional: ["tools", "run", "demo"],
    flags: { input: "{\"name\": \"hello world\"}" },
  });
});

test("command 后的业务 token 原样交给 live schema 二次解析", () => {
  assert.deepEqual(parseArgs(["--instance", "editor-a", "command", "eval", "return 2+2;", "--pdb", "--count=2"], spec), {
    positional: ["command"],
    flags: { instance: "editor-a" },
    passthrough: ["eval", "return 2+2;", "--pdb", "--count=2"],
  });

  assert.deepEqual(parseArgs(["command", "--project-path", "D:/Project", "eval", "--code", "return 1;"], spec), {
    positional: ["command"],
    flags: {},
    passthrough: ["--project-path", "D:/Project", "eval", "--code", "return 1;"],
  });
});
