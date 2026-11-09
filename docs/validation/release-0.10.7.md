# 0.10.7 发布验证

- 目标版本：按用户明确要求递增 c 位，0.10.6 -> 0.10.7。
- 功能提交：1f805f04；发布门禁补充修复提交：864f4cb4。
- 本轮用户已明确批准仅修改 skill-factory/unitycli/manifest.json 的 version、skill-factory/unitycli/reports/trust-report.md 的 Reviewed 行，并接受 release-0.10.7-warnings.md 中全部66条现有描述预算警告。未修改运行时 Skill。
- package.json、Unity Package、治理 manifest 均为0.10.7；package-lock同步；兼容线仍为 >=0.10.0 <0.11.0。
- 自动化影响面：版本相关 package-manager 六个用例（copies unitypkg、canonical manifest、Bridge inspection、orphaned metadata、overwrite version/hash、valid copy-mode check）6/6；治理与投影5/5。功能修复验证见 editor-compile-current-state.md。
- 初次执行整个package-manager文件超过60秒宿主上限，被终止；不计为通过。最终使用上述直接覆盖全部改动版本断言的六个精确选择器重新验证，通过。
- 已执行完整生成/产品TypeScript构建；二次生成 tracked diff 完全一致。将旧 dist 移至固定 Dev Library 缓存后重新生成干净产品 dist，无测试构建文件。
- npm pack --dry-run --ignore-scripts：@alpsckr/unitycli@0.10.7，包内无 dist/tests 或 dist/src；压缩约3.4MB。
- 最终 npm run release:check：pass=true，blockers=[]，66条warnings均已获确认。npm whoami=alpsckr。
- 发布使用 npm publish --access public --ignore-scripts：产品构建已按prepack相同生成/编译顺序完成并校验，避免重复清理已验证输出。
- 尚未执行tag/publish/push时创建本记录；最终registry与本地安装结果在任务答复中报告。
