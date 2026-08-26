import assert from "node:assert/strict";
import test from "node:test";
import { defaultDataDir, isWindows } from "../src/platform.js";

test("isWindows returns true for win32", () => {
  assert.equal(isWindows("win32"), true);
  assert.equal(isWindows("linux"), false);
  assert.equal(isWindows("darwin"), false);
});

test("defaultDataDir uses UNITYCLI_DATA_DIR when set", () => {
  assert.equal(defaultDataDir({ UNITYCLI_DATA_DIR: "/custom/dir" }, "linux"), "/custom/dir");
});

test("defaultDataDir uses LOCALAPPDATA on Windows", () => {
  const result = defaultDataDir({ LOCALAPPDATA: "C:\\Users\\test\\AppData\\Local" }, "win32");
  assert.ok(result.includes("UnityCli"));
  assert.ok(result.includes("Local"));
});

test("defaultDataDir falls back to cwd .unitycli on non-Windows", () => {
  const result = defaultDataDir({}, "linux");
  assert.ok(result.endsWith(".unitycli"));
});
