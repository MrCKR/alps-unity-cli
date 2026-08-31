import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const paths = {
  readers: "scripts/validation/validate-instance-concurrent-readers.mjs",
  multiCli: "scripts/validation/validate-instance-multi-cli.mjs",
  multiPrune: "scripts/validation/validate-instance-multi-prune.mjs",
  dualWriter: "scripts/validation/validate-instance-dual-writer.mjs",
  competingWriter: "scripts/validation/validate-instance-competing-writer.ps1",
  processHelper: "scripts/validation/instance-validation-process.mjs",
  cleanupTests: "unitypkg/Tests/Editor/UnityCliInstanceCleanupTests.cs",
  retryTests: "unitypkg/Tests/Editor/UnityCliInstancePublicationRetryTests.cs",
};
const sources = Object.fromEntries(await Promise.all(Object.entries(paths).map(async ([name, path]) => [name, await readFile(path, "utf8")]))) as Record<keyof typeof paths, string>;

test("instance live validation assets are environment-bound and contain no machine fixture identity", () => {
  for (const [name, source] of Object.entries(sources)) {
    assert.doesNotMatch(source, /D:\\workspace|UnityCli-Dev|issue-0\d+|unity-da1e|processId === \d{3,}/i, `${name} contains a machine fixture`);
  }
  for (const name of ["readers", "multiCli", "multiPrune", "dualWriter"] as const) {
    assert.match(sources[name], /UNITYCLI_DATA_DIR/);
    assert.match(sources[name], /UNITYCLI_INSTANCE_ID/);
    assert.match(sources[name], /\.temp["', ]+.*instance-validation|"instance-validation"/s);
    assert.match(sources[name], /runProcess/);
  }
  assert.match(sources.processHelper, /setTimeout[\s\S]*?child\.kill\(\)[\s\S]*?timed out/);
  assert.match(sources.readers, /process\.on\("exit"[\s\S]*?rmSync/);
  assert.match(sources.dualWriter, /process\.on\("exit"[\s\S]*?rmSync/);
});

test("repeatable multi-process assets cover readers, waiters, mutations, prune, and two writers", () => {
  assert.match(sources.readers, /index < 200/);
  assert.match(sources.readers, /length: 12/);
  assert.match(sources.readers, /invalidRecordCount !== undefined/);
  assert.match(sources.multiCli, /length: 6/);
  assert.match(sources.multiCli, /"500"/);
  assert.match(sources.multiCli, /length: 4/g);
  assert.match(sources.multiCli, /finalCounter/);
  assert.match(sources.multiPrune, /index < 12/);
  assert.match(sources.multiPrune, /length: 6/);
  assert.match(sources.multiPrune, /removed !== fixtures\.length/);
  assert.match(sources.dualWriter, /currentPid = initial\.sessions\[0\]\.processId/);
  assert.match(sources.dualWriter, /sessions\.length !== 2/);
  assert.match(sources.competingWriter, /Local\\UnityCli\.Registry\.\$instanceId/);
  assert.match(sources.competingWriter, /WaitOne\(5000\)/);
});

test("Unity EditMode assets execute real retry and cleanup boundaries", () => {
  assert.match(sources.retryTests, /FileShare\.None/);
  assert.match(sources.retryTests, /TransientWriteFailureUsesLatestStateAndSucceedsOnOneRetry/);
  assert.match(sources.retryTests, /PersistentWriteFailureStopsAtFourAndDoesNotStartAnotherChain/);
  assert.match(sources.cleanupTests, /index < 115/);
  assert.match(sources.cleanupTests, /index < 30/);
  assert.match(sources.cleanupTests, /index < 2001/);
  assert.match(sources.cleanupTests, /TwoBoundedPassesReachOfflineAndDamagedRetentionCaps/);
  assert.match(sources.cleanupTests, /ScanLimitSetsTruncatedAndStillCapsDeletion[\s\S]*?Scanned"\), Is\.EqualTo\(2000\)[\s\S]*?Truncated"\), Is\.True/);
  assert.match(sources.cleanupTests, /RepeatedLogEntriesKeepTwoBoundedSegments[\s\S]*?segments\.Length, Is\.EqualTo\(2\)[\s\S]*?10L \* 1024 \* 1024/);
  assert.match(sources.cleanupTests, /PerInstanceLockDefersThenAllowsTheSameOldRecord/);
  assert.match(sources.cleanupTests, /thread\.Join\(5000\)/);
});
