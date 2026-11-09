# 0.10.7 发布警告确认清单

以下警告来自 release:check。所有条目均为 description 超过 150-token 目标；未超过硬上限，本次不修改这些无关 Tool。

| Tool | 描述 token |
|---|---:|
| component.get | 186 |
| debug.code-execute | 289 |
| debug.code-flow-cancel | 225 |
| debug.code-flow-run | 300 |
| debug.code-flow-submit | 296 |
| debug.code-flow-wait | 298 |
| debug.console-read | 156 |
| debug.events-read | 294 |
| debug.hook-disable | 264 |
| debug.hook-enable | 239 |
| debug.hook-info | 249 |
| debug.hook-install | 299 |
| debug.hook-list | 198 |
| debug.hook-remove | 258 |
| debug.hook-replace | 258 |
| debug.input-info | 280 |
| debug.input-key | 258 |
| debug.input-pointer | 286 |
| debug.input-release | 278 |
| debug.method-find | 260 |
| debug.pause-evaluate | 299 |
| debug.pause-inspect | 251 |
| debug.pause-list | 286 |
| debug.pause-resume | 255 |
| debug.pause-wait | 235 |
| debug.program-info | 269 |
| debug.program-install | 207 |
| debug.program-list | 245 |
| debug.program-remove | 286 |
| debug.program-replace | 287 |
| debug.program-run | 257 |
| debug.program-stop | 268 |
| debug.program-wait | 295 |
| debug.run-begin | 294 |
| debug.run-compare | 293 |
| debug.run-end | 293 |
| debug.run-info | 297 |
| debug.run-list | 291 |
| debug.run-wait | 279 |
| debug.session-create | 291 |
| debug.session-export | 292 |
| debug.session-info | 267 |
| debug.session-remove | 250 |
| debug.session-stop | 283 |
| debug.thread-list | 212 |
| debug.type-schema | 211 |
| debug.value-write | 295 |
| editor.menu-execute | 191 |
| editor.menu-list | 165 |
| editor.refresh | 186 |
| game-object.delete | 220 |
| game-object.find | 167 |
| probuilder.append-vertices-to-edge | 209 |
| probuilder.bevel-edges | 209 |
| probuilder.bridge-edges | 164 |
| probuilder.connect-elements | 173 |
| probuilder.extrude-edges | 165 |
| probuilder.extrude-faces | 155 |
| probuilder.insert-vertex | 221 |
| profiler.memory-snapshot-take | 153 |
| scene.hierarchy | 156 |
| scene.load | 172 |
| screenshot.surround | 152 |
| timeline.clip-add | 173 |
| timeline.create | 151 |
| timeline.info | 199 |

授权请求：接受以上全部现有预算警告，并仅同步以下两个受保护文件：

- skill-factory/unitycli/manifest.json：version 从 0.10.6 改为 0.10.7。
- skill-factory/unitycli/reports/trust-report.md：Reviewed 行改为 Reviewed: 2026-11-09 for release 0.10.7。

不修改 Skill 运行时内容。
