# 会话管家 · Session Hub

> A read-only Windows app that gathers the AI conversation history kept on your computer — Claude,
> ChatGPT (Codex), Gemini and DeepSeek Harness — into one searchable list. Chinese UI.
> Built with the help of ChatGPT, Claude and Gemini.
> **[Download the latest release](../../releases/latest)** · [MIT License](LICENSE)

本项目由 ChatGPT、Claude、Gemini 辅助构建。独立第三方工具，不是 Anthropic、OpenAI、Google 或 DeepSeek 的产品。

## 能做什么

- **一张表看全部对话**：Claude 桌面版的 Code 会话与终端 Claude Code、ChatGPT 桌面版的 Codex、Gemini CLI、
  DeepSeek Harness，以及从官方数据导出导入的 ChatGPT、Claude、Gemini 网页版对话。
- **搜索**：标题、工作目录、备注和对话内容都能搜，可按应用、时间、分组筛选。
- **阅读与导出**：聊天式的对话记录窗口（Markdown、代码块、截图），可导出为 Markdown 或网页。
- **整理**：收藏（★）、置顶、改名、备注、分组、归档，只记在会话管家里，可以撤销。
- **用量与分析**：token 用量统计、单条会话的上下文与缓存分析、两条会话并排对比。
- **接着聊**：一键在原来的应用里打开，或用对应的 CLI 继续。
- **让 AI 查历史**：给 Claude Code、Codex、Claude 桌面版用的只读 MCP 服务。
- **按意思找（可选）**：用你自己放在本机的向量模型做语义搜索。

## 只读，只加不减

- 对各个应用的数据只读，从不改写；会话管家写的文件都在自己的数据目录里：默认 `D:\ClaudeLauncherData`，
  没有 D 盘时用 `%LOCALAPPDATA%\ClaudeSessionLauncher`，也可以用环境变量 `CLAUDE_LAUNCHER_DATA_DIR` 指定。
- 每条记录都存一份压缩副本：应用删掉了自己的记录，这里照样能看、能搜、能导出；只有你在会话管家里删除才会删。
- 不联网、不上传、不自动下载任何东西。

## 下载使用

在 [Releases](../../releases/latest) 下载：

| 文件 | 说明 |
|---|---|
| `SessionHub-<版本>-win-x64.exe` | 整个程序就这一个文件，双击即用 |
| `SessionHub-<版本>-win-x64.zip` | 同一个程序加一份中文使用说明 |
| `SessionHub-Semantic-<版本>-win-x64.zip` | 可选的语义搜索组件，解压到程序旁边 |

- 需要 Windows 10 / 11（64 位）；不用安装，不用另装 .NET，不要管理员权限。
- 程序没有代码签名，第一次运行时 Windows 可能提示「Windows 已保护你的电脑」：点「更多信息」→「仍要运行」。
- 按意思找还需要一个向量模型（推荐 BAAI/bge-small-zh-v1.5 的 ONNX 版），放进 `<数据目录>\models\embedding`。
- 详细说明见 [使用说明](docs/使用说明.txt)。

## 从源码构建

```powershell
dotnet build ClaudeLauncher.slnx
dotnet test ClaudeLauncher.slnx
scripts\Publish-Share.ps1    # 在 dist\ 生成单文件程序和 zip
```

需要 .NET SDK 10。

## 许可证

[MIT](LICENSE)。第三方组件与商标说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
