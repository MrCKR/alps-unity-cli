# URP Tools

The extension contributes independent tools for the active Universal Render Pipeline asset.

| Tool | Description |
| --- | --- |
| `urp.feature-list` | List Renderer Features. |
| `urp.feature-add` | Add one Renderer Feature. |
| `urp.feature-toggle` | Enable or disable one Renderer Feature and verify the re-read active state. |
| `urp.feature-configure` | Set one Renderer Feature property and verify the serialized readback. |
| `urp.feature-remove` | Remove one Renderer Feature and verify it is absent from the renderer collection. |
| `urp.volume-list` | List scene-session volumes. |
| `urp.volume-create` | Create a scene-session volume. |
| `urp.volume-info` | Read one scene-session volume. |
| `urp.volume-add-effect` | Add one effect to an in-memory profile. |
| `urp.volume-set` | Set one effect parameter and return its verified typed serialized readback. |
| `urp.settings-get` | Read active pipeline asset settings. |
| `urp.settings-set` | Set one active pipeline asset setting. |

The project must install `com.unity.render-pipelines.universal` and select an active `UniversalRenderPipelineAsset`.
