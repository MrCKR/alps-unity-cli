import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, mkdtemp, readFile, readdir, rm, stat, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";

const execFileAsync = promisify(execFile);
const scriptPath = resolve("skills", "unitycli", "scripts", "record-feedback.ps1");
const skillPath = resolve("skills", "unitycli", "SKILL.md");
const operationProtocolPath = resolve("skills", "unitycli", "references", "operation-protocol.md");

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

/** 在隔离的 LOCALAPPDATA 下调用 Windows PowerShell 5.1 recorder。 */
async function runFeedback(localAppData: string, args: string[]): Promise<{ stdout: string; stderr: string }> {
  return execFileAsync("powershell.exe", [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    scriptPath,
    ...args,
  ], {
    env: { ...process.env, LOCALAPPDATA: localAppData },
    maxBuffer: 2 * 1024 * 1024,
    windowsHide: true,
  });
}

/** 读取并逐行解析 recorder 生成的 JSONL。 */
async function readJsonLines(path: string): Promise<FeedbackRecord[]> {
  const text = (await readFile(path, "utf8")).replace(/^\uFEFF/, "");
  return text.split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line) as FeedbackRecord);
}

test("feedback recorder writes one bounded local record", async () => {
  const localAppData = await mkdtemp(join(tmpdir(), "unity-cli-feedback-"));
  try {
    const { stdout, stderr } = await runFeedback(localAppData, [
      "-Category", "ambiguity",
      "-Summary", "Descriptor exposed two valid target interpretations.",
      "-Workaround", "Used readback to choose the canonical target.",
      "-Capability", "sample.tool",
      "-ErrorCode", "E_SCHEMA_INVALID",
      "-Phase", "describe",
    ]);
    assert.equal(stderr, "");
    const result = JSON.parse(stdout) as { ok: boolean; recordId: string; path: string; rotated: boolean };
    const expectedPath = join(localAppData, "UnityCli", "feedback", "feedback.jsonl");
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
    assert.deepEqual(await readdir(join(localAppData, "UnityCli", "feedback")), ["feedback.jsonl"]);
  } finally {
    await rm(localAppData, { recursive: true, force: true });
  }
});

test("feedback recorder accepts dotted tool names without misjudging them as hostnames", async () => {
  const localAppData = await mkdtemp(join(tmpdir(), "unity-cli-feedback-toolname-"));
  try {
    const { stdout } = await runFeedback(localAppData, [
      "-Category", "ambiguity",
      "-Summary", "scene.gameobject.components returned two component interpretations.",
      "-Phase", "describe",
    ]);
    const result = JSON.parse(stdout) as { ok: boolean };
    assert.equal(result.ok, true);
  } finally {
    await rm(localAppData, { recursive: true, force: true });
  }
});

test("feedback recorder emits UTF-8 JSON when LOCALAPPDATA contains non-ASCII characters", async () => {
  const parent = await mkdtemp(join(tmpdir(), "unity-cli-feedback-unicode-"));
  const localAppData = join(parent, "本地数据");
  await mkdir(localAppData);
  try {
    const { stdout } = await runFeedback(localAppData, [
      "-Category", "missing",
      "-Summary", "缺少可发现的只读状态。",
      "-Phase", "discovery",
    ]);
    const result = JSON.parse(stdout) as { ok: boolean; path: string };
    assert.equal(result.ok, true);
    assert.equal(result.path, join(localAppData, "UnityCli", "feedback", "feedback.jsonl"));
    const [record] = await readJsonLines(result.path);
    assert.equal(record.summary, "缺少可发现的只读状态。");
  } finally {
    await rm(parent, { recursive: true, force: true });
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
    const localAppData = await mkdtemp(join(tmpdir(), "unity-cli-feedback-invalid-"));
    try {
      await assert.rejects(() => runFeedback(localAppData, args));
      await assert.rejects(() => stat(join(localAppData, "UnityCli", "feedback", "feedback.jsonl")));
    } finally {
      await rm(localAppData, { recursive: true, force: true });
    }
  }
});

test("feedback recorder rejects a UNC LOCALAPPDATA before creating output", async () => {
  await assert.rejects(
    () => runFeedback("\\\\server\\share", [
      "-Category", "ambiguity",
      "-Summary", "Descriptor required manual interpretation.",
      "-Phase", "describe",
    ]),
    (error: unknown) => {
      const stderr = (error as { stderr?: string }).stderr ?? "";
      assert.match(stderr, /fixed local drive/);
      return true;
    },
  );
});

test("feedback recorder rejects reparse points below LOCALAPPDATA", async () => {
  const localAppData = await mkdtemp(join(tmpdir(), "unity-cli-feedback-reparse-"));
  const redirected = await mkdtemp(join(tmpdir(), "unity-cli-feedback-redirected-"));
  try {
    await symlink(redirected, join(localAppData, "UnityCli"), "junction");
    await assert.rejects(
      () => runFeedback(localAppData, [
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
    await assert.rejects(() => stat(join(redirected, "feedback", "feedback.jsonl")));
  } finally {
    await rm(localAppData, { recursive: true, force: true });
    await rm(redirected, { recursive: true, force: true });
  }
});

test("feedback recorder serializes concurrent writers", async () => {
  const localAppData = await mkdtemp(join(tmpdir(), "unity-cli-feedback-concurrent-"));
  try {
    await Promise.all(Array.from({ length: 16 }, (_, index) => runFeedback(localAppData, [
      "-Category", "failure",
      "-Summary", `Concurrent feedback ${index}`,
      "-Capability", "sample.tool",
      "-ErrorCode", "E_TOOL_NOT_FOUND_FOR_TARGET",
      "-Phase", "execute",
    ])));
    const path = join(localAppData, "UnityCli", "feedback", "feedback.jsonl");
    const records = await readJsonLines(path);
    assert.equal(records.length, 16);
    assert.equal(new Set(records.map((record) => record.recordId)).size, 16);
    assert.deepEqual(records.map((record) => record.summary).sort(), Array.from({ length: 16 }, (_, index) => `Concurrent feedback ${index}`).sort());
  } finally {
    await rm(localAppData, { recursive: true, force: true });
  }
});

test("feedback recorder keeps exactly one main file and three bounded archives", async () => {
  const localAppData = await mkdtemp(join(tmpdir(), "unity-cli-feedback-rotate-"));
  try {
    const command = [
      "$ErrorActionPreference='Stop'",
      "$summary='x' * 480",
      "for($i=0;$i -lt 650;$i++){",
      "  & $env:UNITY_CLI_FEEDBACK_SCRIPT -Category ambiguity -Summary ($summary + $i) -Phase discovery | Out-Null",
      "}",
    ].join("\n");
    await execFileAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command], {
      env: { ...process.env, LOCALAPPDATA: localAppData, UNITY_CLI_FEEDBACK_SCRIPT: scriptPath },
      maxBuffer: 2 * 1024 * 1024,
      windowsHide: true,
    });

    const root = join(localAppData, "UnityCli", "feedback");
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
    await rm(localAppData, { recursive: true, force: true });
  }
});

test("Skill feedback contract is opt-in, local, and preserves the cross-session mutation fence", async () => {
  const skill = await readFile(skillPath, "utf8");
  const operationProtocol = await readFile(operationProtocolPath, "utf8");
  const customTools = await readFile(resolve("skills", "unitycli", "references", "custom-tools.md"), "utf8");
  const script = await readFile(scriptPath);
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
  assert.match(skill, /marker 为 `off` 时不得运行脚本/);
  assert.match(skill, /脚本失败不改变 Unity 任务结论/);

  assert.deepEqual([...script.subarray(0, 3)], [0xef, 0xbb, 0xbf]);
  const scriptText = script.toString("utf8");
  assert.match(scriptText, /UnityCli\\feedback/);
  assert.match(scriptText, /Local\\UnityCliFeedback_v1/);
  assert.doesNotMatch(scriptText, /Invoke-WebRequest|Invoke-RestMethod|HttpClient|WebClient|Start-BitsTransfer/i);
});
