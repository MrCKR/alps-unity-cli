import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function source(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

test("asset move and rename validate paths before committing and read back final asset state", async () => {
  const [move, rename] = await Promise.all([
    source("Tools/asset/move.cs"),
    source("Tools/asset/rename.cs")
  ]);

  assert.match(move, /RequireExistingAssetPath\(request\.Source \?\? request\.Path\)[\s\S]*RequireAssetPath\(request\.Destination\)[\s\S]*AssetExists\(destination\)[\s\S]*ctx\.CommitEffect\(\)[\s\S]*AssetDatabase\.MoveAsset/s);
  assert.match(move, /E_ASSET_MOVE_STATE_MISMATCH/);
  assert.match(rename, /AssetPathToGUID\(path\)[\s\S]*ctx\.CommitEffect\(\)[\s\S]*AssetDatabase\.RenameAsset/s);
  assert.match(rename, /E_ASSET_RENAME_STATE_MISMATCH/);
});

test("asset delete avoids a global refresh and proves the terminal file and AssetDatabase state", async () => {
  const remove = await source("Tools/asset/delete.cs");

  assert.match(remove, /ctx\.CommitEffect\(\);\s*if \(!AssetDatabase\.DeleteAsset\(path\)\)/s);
  assert.match(remove, /ctx\.Wait\.Until[\s\S]*!File\.Exists\(fullPath\)[\s\S]*!Directory\.Exists\(fullPath\)[\s\S]*!AssetDatabase\.IsValidFolder\(path\)[\s\S]*LoadMainAssetAtPath\(path\)/s);
  assert.doesNotMatch(remove, /AssetDatabase\.(?:Refresh|AssetPathToGUID)/);
});

test("pipeline settings never clears an omitted request and proves the observed pipeline", async () => {
  const pipeline = await source("Tools/rendering/pipeline/settings.cs");

  assert.match(pipeline, /if \(string\.IsNullOrEmpty\(r\.RenderPipeline\)\)[\s\S]*return new/s);
  assert.match(pipeline, /var pipeline = resetToBuiltIn \? null : AssetDatabase\.LoadAssetAtPath<RenderPipelineAsset>/);
  assert.match(pipeline, /ctx\.CommitEffect\(\);\s*GraphicsSettings\.defaultRenderPipeline = pipeline;/s);
  assert.match(pipeline, /var observedPipeline = GraphicsSettings\.defaultRenderPipeline/);
  assert.match(pipeline, /E_RENDER_PIPELINE_STATE_MISMATCH/);
});
