import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const sources = [
  "unitypkg/Editor/Tools/editor/active-tool.cs",
  "unitypkg/Editor/Tools/editor/active-tool-set.cs",
  "unitypkg/Editor/Tools/editor/tool-states.cs",
];

test("Editor Tools API 使用完整限定名", async () => {
  for (const path of sources) {
    const source = await readFile(path, "utf8");
    assert.doesNotMatch(source, /(?:=|return)\s+Tools\.(?:current|handlePosition|handleRotation|pivotMode|pivotRotation)\b/, path);
    assert.doesNotMatch(source, /^\s*Tools\.current\s*=/m, path);
  }
});
