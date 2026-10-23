import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, readdir, rm, stat, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";

const execFileAsync = promisify(execFile);
const skillPath = resolve("skills", "unitycli", "SKILL.md");
const skillManifestPath = resolve("skill-factory", "unitycli", "manifest.json");
const operationProtocolPath = resolve("skills", "unitycli", "references", "operation-protocol.md");
const feedbackReferencePath = resolve("skills", "unitycli", "references", "feedback.md");
const scriptName = "record-feedback.ps1";
const skillScriptPath = resolve("skills", "unitycli", "scripts", scriptName);

interface FeedbackRecord {
  schemaVersion: number;
  recordId: string;
  createdAtUtc: string;
  category: string;
  phase: string;
  capability: string | null;
  errorCode: string | null;
  summary: string;
  workaround: string | null;
  resolved: boolean;
}

/**
 * 构造隔离的 Skill 安装夹具：根目录含 SKILL.md，recorder 位于 scripts/。
 * 经验库由脚本写入夹具根的 experience/，因此测试不会触碰仓库内的 Skill 目录。
 */
async function createSkillFixture(prefix: string, leafName?: string): Promise<string> {
  const parent = await mkdtemp(join(tmpdir(), prefix));
  const root = leafName ? join(parent, leafName) : parent;
  if (leafName) await mkdir(root);
  await mkdir(join(root, "scripts"), { recursive: true });
  await cp(skillPath, join(root, "SKILL.md"));
  await cp(skillScriptPath, join(root, "scripts", scriptName));
  return root;
}

/** 经验库固定位于 Skill 根目录下的 experience/。 */
function feedbackDir(skillRoot: string): string {
  return join(skillRoot, "experience");
}

/** 在隔离的 Skill 夹具内调用 Windows PowerShell 5.1 recorder。 */
async function runFeedback(skillRoot: string, args: string[]): Promise<{ stdout: string; stderr: string }> {
  return execFileAsync("powershell.exe", [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    join(skillRoot, "scripts", scriptName),
    ...args,
  ], {
    env: { ...process.env },
    maxBuffer: 2 * 1024 * 1024,
    windowsHide: true,
  });
}

/** 读取并逐行解析 recorder 生成的 JSONL。 */
async function readJsonLines(path: string): Promise<FeedbackRecord[]> {
  const text = (await readFile(path, "utf8")).replace(/^\uFEFF/, "");
  return text.split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line) as FeedbackRecord);
}

test("feedback recorder writes one bounded record inside the installed Skill", async () => {
  const skillRoot = await createSkillFixture("unity-cli-feedback-");
  try {
    const { stdout, stderr } = await runFeedback(skillRoot, [
      "-Category", "ambiguity",
      "-Summary", "Descriptor exposed two valid target interpretations.",
      "-Workaround", "Used readback to choose the canonical target.",
      "-Capability", "sample.tool",
      "-ErrorCode", "E_SCHEMA_INVALID",
      "-Phase", "describe",
    ]);
    assert.equal(stderr, "");
    const result = JSON.parse(stdout) as { ok: boolean; recordId: string; path: string; rotated: boolean };
    const expectedPath = join(feedbackDir(skillRoot), "feedback.jsonl");
    assert.equal(result.ok, true);
    assert.equal(result.path, expectedPath);
    assert.equal(result.rotated, false);

    const records = await readJsonLines(expectedPath);
    assert.equal(records.length, 1);
    assert.deepEqual(records[0], {
      schemaVersion: 1,
      recordId: result.recordId,
      createdAtUtc: records[0].createdAtUtc,
      category: "ambiguity",
      phase: "describe",
      capability: "sample.tool",
      errorCode: "E_SCHEMA_INVALID",
      summary: "Descriptor exposed two valid target interpretations.",
      workaround: "Used readback to choose the canonical target.",
      resolved: true,
    });
    assert.match(records[0].createdAtUtc, /^\d{4}-\d{2}-\d{2}T/);
    assert.deepEqual(await readdir(feedbackDir(skillRoot)), ["feedback.jsonl"]);
  } finally {
    await rm(skillRoot, { recursive: true, force: true });
  }
});

test("feedback recorder accepts dotted tool names without misjudging them as hostnames", async () => {
  const skillRoot = await createSkillFixture("unity-cli-feedback-toolname-");
  try {
    const { stdout } = await runFeedback(skillRoot, [
      "-Category", "ambiguity",
      "-Summary", "scene.gameobject-components returned two component interpretations.",
      "-Phase", "describe",
    ]);
    const result = JSON.parse(stdout) as { ok: boolean };
    assert.equal(result.ok, true);
  } finally {
    await rm(skillRoot, { recursive: true, force: true });
  }
});

test("feedback recorder emits UTF-8 JSON when the Skill path contains non-ASCII characters", async () => {
  const skillRoot = await createSkillFixture("unity-cli-feedback-unicode-", "技能库");
  try {
    const { stdout } = await runFeedback(skillRoot, [
      "-Category", "missing",
      "-Summary", "缺少可发现的只读状态。",
      "-Phase", "discovery",
    ]);
    const result = JSON.parse(stdout) as { ok: boolean; path: string };
    assert.equal(result.ok, true);
    assert.equal(result.path, join(feedbackDir(skillRoot), "feedback.jsonl"));
    const [record] = await readJsonLines(result.path);
    assert.equal(record.summary, "缺少可发现的只读状态。");
  } finally {
    await rm(skillRoot, { recursive: true, force: true });
  }
});

test("feedback recorder rejects secrets, paths, multiline input, and incomplete workaround", async () => {
  const invalidCases: string[][] = [
    ["-Category", "failure", "-Summary", "Authorization: Bearer hidden"],
    ["-Category", "failure", "-Summary", "token=abcdef123456"],
    ["-Category", "failure", "-Summary", "token abcdef123456"],
    ["-Category", "failure", "-Summary", "password correcthorsebatterystaple"],
    ["-Category", "failure", "-Summary", "Bearer eyJhbGciOiJub25l.eyJzdWIiOiJ1c2Vy.signature"],
    ["-Category", "failure", "-Summary", "Service api.internal.example:443 failed"],
    ["-Category", "failure", "-Summary", "Service api.internal.example failed"],
    ["-Category", "failure", "-Summary", "Service worker:5000 failed"],
    ["-Category", "failure", "-Summary", "Read file://buildhost/share/key.txt"],
    ["-Category", "failure", "-Summary", "Failed at C:\\private\\file.txt"],
    ["-Category", "failure", "-Summary", "Read Assets/Private/key.txt"],
    ["-Category", "failure", "-Summary", "Share \\\\server\\private\\key.txt failed"],
    ["-Category", "failure", "-Summary", "Opaque 0123456789abcdef0123456789abcdef"],
    ["-Category", "failure", "-Summary", "first\nsecond"],
    ["-Category", "failure", "-Summary", "endpoint: 127.0.0.1:9876"],
    ["-Category", "failure", "-Summary", "   "],
    ["-Category", "failure", "-Summary", "x".repeat(513)],
    ["-Category", "workaround", "-Summary", "A workaround was needed."],
  ];
  for (const args of invalidCases) {
    const skillRoot = await createSkillFixture("unity-cli-feedback-invalid-");
    try {
      await assert.rejects(() => runFeedback(skillRoot, args));
      await assert.rejects(() => stat(join(feedbackDir(skillRoot), "feedback.jsonl")));
    } finally {
      await rm(skillRoot, { recursive: true, force: true });
    }
  }
});

test("feedback recorder refuses a script that does not sit under a Skill root", async () => {
  const skillRoot = await createSkillFixture("unity-cli-feedback-noroot-");
  try {
    await rm(join(skillRoot, "SKILL.md"), { force: true });
    await assert.rejects(
      () => runFeedback(skillRoot, [
        "-Category", "ambiguity",
        "-Summary", "Descriptor required manual interpretation.",
        "-Phase", "describe",
      ]),
      (error: unknown) => {
        const stderr = (error as { stderr?: string }).stderr ?? "";
        assert.match(stderr, /SKILL\.md/);
        return true;
      },
    );
    await assert.rejects(() => stat(join(feedbackDir(skillRoot), "feedback.jsonl")));
  } finally {
    await rm(skillRoot, { recursive: true, force: true });
  }
});

test("feedback recorder rejects a reparse point on the experience path", async () => {
  const skillRoot = await createSkillFixture("unity-cli-feedback-reparse-");
  const redirected = await mkdtemp(join(tmpdir(), "unity-cli-feedback-redirected-"));
  try {
    await symlink(redirected, feedbackDir(skillRoot), "junction");
    await assert.rejects(
      () => runFeedback(skillRoot, [
        "-Category", "ambiguity",
        "-Summary", "Descriptor required manual interpretation.",
        "-Phase", "describe",
      ]),
      (error: unknown) => {
        const stderr = (error as { stderr?: string }).stderr ?? "";
        assert.match(stderr, /must not contain reparse points/);
        return true;
      },
    );
    await assert.rejects(() => stat(join(redirected, "feedback.jsonl")));
  } finally {
    await rm(skillRoot, { recursive: true, force: true });
    await rm(redirected, { recursive: true, force: true });
  }
});

test("feedback recorder serializes concurrent writers", async () => {
  const skillRoot = await createSkillFixture("unity-cli-feedback-concurrent-");
  try {
    await Promise.all(Array.from({ length: 16 }, (_, index) => runFeedback(skillRoot, [
      "-Category", "failure",
      "-Summary", `Concurrent feedback ${index}`,
      "-Capability", "sample.tool",
      "-ErrorCode", "E_TOOL_TARGET_NOT_FOUND",
      "-Phase", "execute",
    ])));
    const path = join(feedbackDir(skillRoot), "feedback.jsonl");
    const records = await readJsonLines(path);
    assert.equal(records.length, 16);
    assert.equal(new Set(records.map((record) => record.recordId)).size, 16);
    assert.deepEqual(records.map((record) => record.summary).sort(), Array.from({ length: 16 }, (_, index) => `Concurrent feedback ${index}`).sort());
  } finally {
    await rm(skillRoot, { recursive: true, force: true });
  }
});

test("feedback recorder keeps exactly one main file and three bounded archives", async () => {
  const skillRoot = await createSkillFixture("unity-cli-feedback-rotate-");
  try {
    const command = [
      "$ErrorActionPreference='Stop'",
      "$summary='x' * 480",
      "for($i=0;$i -lt 650;$i++){",
      "  & $env:UNITY_CLI_FEEDBACK_SCRIPT -Category ambiguity -Summary ($summary + $i) -Phase discovery | Out-Null",
      "}",
    ].join("\n");
    await execFileAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command], {
      env: { ...process.env, UNITY_CLI_FEEDBACK_SCRIPT: join(skillRoot, "scripts", scriptName) },
      maxBuffer: 2 * 1024 * 1024,
      windowsHide: true,
    });

    const root = feedbackDir(skillRoot);
    const names = (await readdir(root)).sort();
    assert.deepEqual(names, ["feedback.1.jsonl", "feedback.2.jsonl", "feedback.3.jsonl", "feedback.jsonl"]);
    let recordCount = 0;
    for (const name of names) {
      const path = join(root, name);
      assert.ok((await stat(path)).size <= 64 * 1024);
      const records = await readJsonLines(path);
      recordCount += records.length;
      assert.ok(records.every((record) => record.schemaVersion === 1 && record.summary.length <= 512));
    }
    assert.ok(recordCount > 0 && recordCount < 650);
  } finally {
    await rm(skillRoot, { recursive: true, force: true });
  }
});

test("Skill feedback contract is opt-in, local, and preserves the cross-session mutation fence", async () => {
  const skill = await readFile(skillPath, "utf8");
  const operationProtocol = await readFile(operationProtocolPath, "utf8");
  const feedbackReference = await readFile(feedbackReferencePath, "utf8");
  const customTools = await readFile(resolve("skills", "unitycli", "references", "custom-tools.md"), "utf8");
  const script = await readFile(skillScriptPath);
  assert.equal((skill.match(/^FeedbackMode:\s*(?:off|on)$/gm) ?? []).length, 1);
  assert.match(skill, /^FeedbackMode: off$/m);
  assert.match(skill, /tools search '<k1\|k2\|\.\.\.>'/);
  assert.match(skill, /按需发现对应域的`\.status` Tool并只传原root `requestId`/);
  assert.match(skill, /其他能力使用只读Tool或业务readback形成新的业务决策/);
  assert.match(skill, /首次mutation POST后Editor session改变时也不会向新session重投/);
  assert.match(skill, /transient:true.*nextActions.*仍是建议，不构成额外授权门/);
  assert.match(operationProtocol, /只有形成新的业务决策后才提交新请求/);
  assert.match(operationProtocol, /editorSessionId.*未改变/);
  assert.match(operationProtocol, /禁止向新session再次POST/);
  assert.match(customTools, /OutcomeUnknownPolicy = "caller-decides-after-timeout"/);
  assert.match(skill, /正常无摩擦调用不记录/);
  // 记录条件有两个独立来源：模式开关，或用户在当前请求中的明确要求。
  assert.match(skill, /用户在当前请求中明确要求记录反馈时/);
  assert.match(skill, /marker 为 `off` 且用户未明确要求时不得运行脚本/);
  assert.match(skill, /不得把用户的可能意图推断为明确要求/);
  assert.match(skill, /脚本失败不改变 Unity 任务结论/);

  const manifest = JSON.parse(await readFile(skillManifestPath, "utf8")) as {
    output_contract: { feedback_summary_condition: string };
    permissions: { file_write: string[]; file_write_scope?: string };
    runtime_projection: { runtime_state: string[] };
  };
  assert.match(manifest.output_contract.feedback_summary_condition, /the user explicitly asked for a record in the current request/);
  // 经验库是安装目录内的状态区：写入面是相对路径，且不进入投影清单。
  assert.deepEqual(manifest.permissions.file_write, [
    "experience/feedback.jsonl",
    "experience/feedback.1.jsonl",
    "experience/feedback.2.jsonl",
    "experience/feedback.3.jsonl",
  ]);
  assert.equal(manifest.permissions.file_write_scope, "installed Skill directory");
  assert.ok(manifest.runtime_projection.runtime_state.includes("experience/**"));
  assert.match(feedbackReference, /experience\\/);
  assert.match(feedbackReference, /状态区/);

  assert.deepEqual([...script.subarray(0, 3)], [0xef, 0xbb, 0xbf]);
  const scriptText = script.toString("utf8");
  // 记录根必须由脚本位置推导，且必须证明脚本位于 Skill 根目录。
  assert.match(scriptText, /\$PSScriptRoot/);
  assert.match(scriptText, /Join-Path \$skillRoot "experience"/);
  assert.match(scriptText, /SKILL\.md/);
  assert.doesNotMatch(scriptText, /LOCALAPPDATA/);
  assert.match(scriptText, /Local\\UnityCliFeedback_v1/);
  assert.doesNotMatch(scriptText, /Invoke-WebRequest|Invoke-RestMethod|HttpClient|WebClient|Start-BitsTransfer/i);
});
