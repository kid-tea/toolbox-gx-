# Toolbox 2.0.0

发布日期：待定

## 大版本重点：AI Agent 检验

- 新增 `AI Agent 检验` 功能页，用于检查本机 AI Agent 开发环境。
- 深度支持 Codex 与 Claude Code 的本地环境检测。
- 浅检测 Gemini、Cursor、Continue、Cline/Roo 等常见 Agent 相关目录。
- 检测本地 Skills，并按三级结构展示：AI Agent -> 插件/其它 Skill -> Skill。
- Skill 说明支持中文 / English 切换；用户自定义 Skill 可维护说明，插件缓存 Skill 保持只读。
- 检测项目规则文件，例如 `AGENTS.md`、`CLAUDE.md`、`.codex`、`.claude`。

## Token 与报告

- Codex：从 `.codex/state_5.sqlite` 只读读取线程级 token 元数据。
- Claude Code：从 `.claude/stats-cache.json` 读取每日活动和模型 token 汇总。
- Token 页面按今日、本周、本月和模型维度展示统计。
- 费用显示为本地估算，不代表 OpenAI 或 Anthropic 实际账单。
- 支持导出 Markdown 检测报告，便于记录和排查环境状态。

## 隐私与安全边界

- 不修改任何 Agent 配置文件。
- 不读取 `auth.json` 中的密钥内容，只判断文件存在性。
- 不读取聊天正文。
- 不导出 API Key、聊天内容等敏感信息。
- 不做后台自动扫描，用户点击开始扫描后才读取本机元数据。

## 视觉与交互

- AI Agent 检验页采用独立的深色仪表盘信息架构。
- Skills 树默认折叠，避免信息一次性堆满页面。
- 页面提供环境概览、Skills / 规则、Token 消耗、报告等分区。

## 说明

- 2.0.0 是 AI Agent 能力的大版本发布线。
- 1.0.4 仍作为非 AI 功能和 UI 体验优化版本发布。
