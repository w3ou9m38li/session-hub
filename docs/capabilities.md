> **0.18.0（2026-10-13）说明**：读写边界不变。`CliProbeCache.Load/Save`（数据目录 `probe-cache.json`，`ProbeRules`
> 版本号不符或损坏即空），`CatalogSyncService(…, probeCacheFile)` 在读完终端记录后有变化才存。`Read(…, newestTranscripts)`
> 与 `CliMetadataReader.Read(…, newest)` 只读最后写的 N 个、不 `Retain`、终端来源记 Partial（不进 `Complete`，不标消失）；
> 窗口在目录为空时先这样读 300 个（`FirstLookAsync`）。目录在补列之后建 `sessions_native` 与 `merged_into` /
> `continued_in` / `resumed_from` 的非空部分索引，`AdoptMergedPreferences` 改为 CROSS JOIN 从有偏好的并入行出发。
> `AttachContentIndex` 只记路径，`PrepareContentSearch` 填临时表前 ATTACH、填完 DETACH。`ThrottledProgress` 把后台进度限为
> 每 250 ms 一次。
>
> **0.17.0（2026-10-13）说明**：读写边界不变。阅读器的发言可多选：`MessageViewModel.IsSelected`（列表回收气泡，状态
> 放在视图模型上），气泡标题行的勾选按钮 `SelectMessage_Click`（Shift 以 `_selectionAnchor` 连选，只算发言），选中时
> 底部 `SelectionButtons` 取代整条对话的按钮；复制用 `TranscriptExport.ToPlainText`，导出走与整条相同的 `ExportAsync`，
> 内容为选中的条目、标题加「（节选 N 条）」。末尾预览换成整条时按（序号、角色、正文）把选择带过去。
>
> **0.16.0（2026-10-12）说明**：只加不减。`SqliteCatalog.MarkGone` 取代 `RemoveMissing`：完整读到的来源里没有的行
> 记 `source_gone_utc`（新列，旧库自动加），再出现时 upsert 清掉；`Delete` 删行（连同 `merged_into` 指向它的）并写
> `deleted_sessions`（新表），upsert 跳过其中的键；`Forget(backend)` 只用于移除导入。`ContentIndex.Update(…, listed)`
> 只移除不再列出的对话。`TranscriptArchive`（数据目录 `archive\`，`archive.sqlite` 记 family+conversation → 源路径、
> 源长度与修改时间、副本长度）：JSON lines 记录变长时先比对副本末尾 4 KB 再追加到最后一个换行，否则整份重抄；`.json`
> 文档静置一分钟后整份抄；最旧的先抄，每轮 20 s；`copies\` 用 `FSCTL_SET_COMPRESSION` 开 NTFS 压缩。
> `TranscriptLocator.Archive`：查找结果是 `.zst` 时换成解压后的副本（`Unpacked`），找不到原文件时用副本（`CopyOf`）。
> `TranscriptFiles.OpenRead` 用 ZstdSharp.Port 0.8.8（纯托管、MIT）解压；`CodexMetadataReader` 认数据库路径加 `.zst`
> 与 `rollout-*.jsonl.zst`。`CatalogSyncService` 拆成 `Read`（不碰目录）与 `Apply`（写），窗口只在 Apply 时占目录锁；
> 后台线程 `SetThreadPriority(THREAD_MODE_BACKGROUND_BEGIN)`。Takeout HTML：`outer-cell` 卡片、`content-cell` 三格，
> 时间 `MMM d, yyyy, h:mm:ss AM/PM ZZZ` 等按缩写表换算；记录键改为秒级时间 + 对话 + 空白统一的提问，载入时重算。
> `BackendFilterKind.Gemini` 同时含 gemini 与 gemini-web。
>
> **0.15.0（2026-10-12）说明**：新增来源 Gemini 网页版（`ConversationKey.GeminiWeb = "gemini-web"`），读写边界
> 不变：只读用户选中的 Google Takeout 导出，写的只有自己数据目录里的 `gemini-web\`。`GeminiTakeout.Read` 只看路径里
> 含 Gemini / Bard 的文件夹（`My Activity\Gemini Apps\MyActivity.json`），记录数组里 `title` 以 Prompted 开头的是提问，
> `safeHtmlItem[].html` 是回答，`details[].url` 里 `gemini.google.com/app/<十六进制>` 是对话 ID，`attachedFiles` / `imageFile`
> 是同目录的文件，`subtitles` 里的网页（Chrome 侧边栏分享的标签页）、`lh3.googleusercontent.com` 生成图片链接与
> 「Used skill」说明分别保留；其余（Cleared 等）跳过。`GeminiTakeoutStore`：`entries.json`（按 时间+对话+提问 的哈希合并，
> 新导入覆盖同键、旧的保留）、`files\<内容哈希>`、`conversations\session-<id>.json`（Gemini CLI 文档格式，内容不变不重写，
> 消失的删掉）。`GeminiSession` 认自己写的 `launcherFile` 部件，只从文档旁 `..\files\` 读图片。`HtmlMarkdown` 把回答转
> Markdown（只留 http(s) 链接）。`GeminiMetadataReader.ForTakeout` 列行（无工作目录、无 CLI ID）；`AppStanding.Imported = 5`
> （「导入」，`Unobserved` 改为 6，SQL 同步）、`SourceRank` 5、`BackendFilter.GeminiWeb`；无链接的记录按 30 分钟间隔拼段
> （ID 以 t 开头，不可打开）；`GeminiWebLink` 只把十六进制 ID 拼进 `https://gemini.google.com/app/<id>`；用量不计。
>
> **0.14.0（2026-10-12）说明**：新增只读来源 Gemini CLI（`ConversationKey.Gemini = "gemini"`），读写边界不变：只读
> `~\.gemini\tmp\*\chats\session-*.json` 与各项目文件夹的 `.project_root`，同目录的 `oauth_creds.json` 从不打开；
> 唯一的动作是 `gemini --resume <UUID>`（`GeminiSessionLauncher`，参数按列表传、只认 UUID）。`GeminiSession` 解析
> 整份文档（会话是随对话整份重写的一个 JSON，解析失败当作 `IOException`：写到一半，下次再试）；
> `GeminiMetadataReader` 每个会话一行（摘要 → 第一句话 → ID 作标题，同一会话多份文件取后写的，
> `GeminiProbeCache` 按长度与修改时间缓存文件头）。`CatalogSyncService` 只在构造时给了 `GeminiPaths` 且文件夹存在时
> 才读（测试从不读本机数据），有读不了的文件时记 Partial、不 `RemoveMissing`。`TranscriptReader` 对 `.json` 按条目
> 分页、末尾与最近对话整份读；`ContentIndex` 与 `UsageLedger` 对 Gemini 不做追加读，每次整份重读后替换。
> 用量：`Input = max(0, input − cached) + tool`，`CacheRead = cached`，`Output = output + thoughts`，调用键为
> `gemini:` + 消息 ID。界面：第三种来源色（`GeminiBrush` / `GeminiTextBrush` / `GeminiFillBrush`）、
> `BackendFilter.Gemini`、按来源分区排第 4、用量窗口第三段柱与筛选（`UsageApp.Gemini = 2`，设置里保留 "Gemini"）；
> 「在应用中打开」不可用，「改动的文件」说明不记录。
>
> **0.13.0（2026-10-12）说明**：读写边界不变。阅读器 `BubbleTemplate` 标题行加复制按钮（`CopyMessage_Click`，
> 复制 `MessageViewModel.Text` 全文而非截断后的 `ShownText`）；「刚复制」的对勾由 `MessageViewModel.JustCopied`
> 驱动 1.5 秒——列表是回收式虚拟化，按钮会被别的消息复用，状态不能放在按钮上。
>
> **0.12.0（2026-09-28）说明**：读写边界不变。最近的搜索：`Settings/SearchHistory` 只管列表规则——留的形式
> （`Normalize`：去空白，1 个字符与超过 200 字的不留）、按 `SearchKeys.Normalize` 判同、最多 12 条、`Typing` 让同一次
> 输入的各阶段互相替换（已在列表里的词只前移、不被替换）、`Clean` 清洗读入的列表；存 `LauncherSettings.RecentSearches`
> 与 `RememberSearches`（关闭时 `Sanitize` 丢弃列表）。窗口：`SearchHistoryPanel` 是弹出层内容，键盘留在搜索框、由它
> 移动高亮；词停留 2 秒或搜索框失去焦点时记下，「定位」放入的 ID 不记；点空搜索框、时钟按钮或 Alt+↓ 打开
> （`StaysOpen=False`，关闭后 300 ms 内的同一次点击不再打开）。
>
> **0.11.3（2026-09-24）说明**：读写边界不变。窗口程序：同步时的旋转图标由 `MainWindow.ShowSyncSpin` 自己创建并保存
> `AnimationClock`，同步结束时 `Controller.Stop()` 再 `ApplyAnimationClock(…, null)`——原来的 `BeginAnimation(…, null)`
> 只摘不停，`RepeatBehavior.Forever` 的时钟在被回收前一直让 WPF 逐帧渲染；窗口最小化或隐藏时不转（`StateChanged` /
> `IsVisibleChanged` 时重新判断）。诊断记录：界面线程上每 10 秒一次、以内核时间为主的 0.1–0.6 s 停顿来自搜狗拼音
> （SogouPY.ime 16.6）在本线程建的定时器（`SoPY_UI` 窗口，`WM_TIMER` 0x7D）；平时每次约 1 ms，个别进程里每次遍历
> `CTF\TIP\…\LanguageProfile` 与 `HKCR\CLSID\…\InProcServer32`，与遮挡、渲染无关。
>
> **0.11.2（2026-09-24）说明**：读写边界不变。`Backends/PooledBytes`：读块（1 MB）与行缓冲从一个自己的有界池
> （`ArrayPool.Create(1 MB, 4)`，不用会按处理器留大数组的共享池）借还；`ContentIndex.Update` 一整遍共用一个行缓冲
> （`TranscriptReader.ReadDialogueFrom` 的内部重载，每份记录开头清空）。对话读取对 64 KB 以上的行先看开头 8 KB，
> 是工具流量就跳过、不再先攒；`UsageScanner` 对 16 KB 以上的行只在开头含 assistant 角色（Claude）或
> token_count / turn_context / session_meta（Codex）时才留。窗口程序：同步后 `RunBackgroundPassesAsync` 先内容
> 索引、后用量账本；`MemoryRelief` 在自上次以来分配满 256 MB 且托管堆提交 ≥ 64 MB 时，等用户停手做一次
> `GCCollectionMode.Aggressive` 回收（两遍跑完先等 2 秒让后台线程退出；另每分钟检查一次）。
>
> **0.11.1（2026-09-24）说明**：`Backends/OwnPart`——一份记录里哪部分是这条会话自己的：续接副本是它自己会话 ID
> 下的行，Codex 分叉是分叉那一刻整块写入之后的行（以第一行时间为锚，遇 `thread_settings_applied` 或过两秒即新
> 内容），内容索引（`Source.Part`，沿用 `only_session` 列存 `@after-fork`）、`ChangeScanner.Read(part)`、
> `MemoryCandidateExtractor.Extract(part)` 共用；Codex 读取器从 rollout 第一行记 `forked_from_id`（进程内缓存）
> 存入 `resumed_from`，分叉仍是独立任务、不并入，只在父任务还在且有记录文件时保留链接。
>
> **0.11.0（2026-09-24）说明**：读写边界不变。`claude --resume` 的续接副本：`CliMetadataReader` 记下记录开头几行的
> `sessionId`（≠ 文件名即续接副本），对副本从文件末尾找最后一条消息看是否它自己写的（`SaidAnythingOfItsOwn`，随
> 探测缓存）；`CatalogSyncService.FoldResumedCopies` 把续接后没说话的副本并入原会话（`merged_into`，经桌面会话、
> 副本的副本解析到最终列出的行；原会话不在的副本保持独立），新列 `resumed_from`；`AdoptMergedPreferences` 的
> 目标放宽到终端行；`SqliteCatalog.Resumes`（续接自 / 从这里续接 / 已并入几份）。内容索引 `Source.OnlySession`
> 与布局 3（副本只收自己写的行，首次打开重建），`ChangeScanner.Read(onlySession)`；
> `ClaudeCodeMarkup.PeelInjected`（消息开头的 system-reminder、ide_opened_file、ide_selection 单独折叠，后面的话
> 仍是用户的），`TitleText.Tidy`（从文字推出的标题只留 @ 提到的文件名与链接文字）。
>
> **0.10.1（2026-09-24）说明**：续接链双向可达。`SqliteCatalog.ContinuationChain`（从桌面会话或它任一段续接前的
> 记录出发，取出整条链：续接前的记录按最后活动排先后，桌面会话在最后）；行查询多一个只对桌面会话计算的子查询
> 列 `EarlierParts`（列表的「续接 N」）；`Core/Display/ContinuationNames`（各段的叫法与阅读器说明行）；详情面板
> 「续接」一行与阅读器标题下的说明行，打开对话记录统一走 `OpenTranscriptFor`。
>
> **0.10.0（2026-09-24）说明**：读写边界不变（对两款应用只启动、不写）。新增：`Launch/ChatGptDeepLink`
> （`codex://threads/<id>`，交给正在运行的那份 ChatGPT：Codex++ 托管副本用它自己的程序再启动一次转发，商店版由
> Windows 跟随链接；都没运行时经 Codex++ 快捷方式启动、等到窗口再转发；已实测）；`ClaudeDeepLink.BuildResumeCli`
> 改为主机 `resume`（`claude://resume?session=…`，读自 Claude 2.7032 的处理代码，未实际导入）；`Launch/QuickOpen`
> 与任务栏跳转列表（WPF `JumpList`；条目以 `--open <backend> <id>` / `--launch claude|chatgpt` 重新启动本程序，
> 不开窗口、不经单实例、不读目录，id 按链接的格式校验；设置 `JumpList` 可只留两个应用）；打开前
> `AllowSetForegroundWindow(ASFW_ANY)`；桌面会话的 `priorCliSessionIds` → 终端行新列 `continued_in`（仍然列出，
> `AppStanding.EarlierPart`「续接前」，「在 Claude 中打开」走它所属桌面会话的链接）；主窗口改在 `OnStartup` 末尾
> 显式创建（`StartupUri` 在调用过 `Shutdown()` 的提前退出路径上仍会构造主窗口）；详情面板的用量行
> （`UsageLedger.Totals(conversation)`）。
>
> **0.9.0（2026-09-23）说明**：读写边界不变。新增：搜索语法 `SearchKeys.Parse`（引号整句、`-` 排除）；
> 相关度排序 `SortField.Relevance`（元数据全中者在前，再按命中消息数；每词临时表 `said_N(conversation, hits)`，
> 「只搜我说的」另用 `role='user'` 的表）；阅读器逐处跳转；用量账本 `Usage/UsageLedger`（独立的 `usage.sqlite`，
> 只读 Claude `message.usage` 与 Codex `token_count`，按调用键全局去重、旧文件优先，子代理与子任务归到所属会话）
> 与用量窗口；`Changes/ChangeScanner` 与「改动的文件」窗口（Claude `structuredPatch`、Codex `patch_apply_end`
> / apply_patch 文本）；`Core/Display/MarkdownHtml` 与自包含网页导出（CSP 禁外部资源、无脚本）；内容索引可关闭与
> 重建；自定义全局快捷键；桌面 Code 深链接已端到端验证。
>
> **0.8.0（2026-09-23）说明**：查找与阅读，读写边界不变（只写本工具数据目录）。新增：对话内容全文索引
> `Search/ContentIndex`（独立的 `search-index.sqlite`，FTS5 trigram；只索引用户与助手的文字；按文件长度与修改
> 时间增量，变长从字节偏移续读，rowid = (会话 id << 32) + 序号；目录库 ATTACH 后每个搜索词查一次存入临时表）；
> 日期分段与时间筛选（`DateBuckets`、`since`）；本工具内的置顶、名字、备注（`local_title/local_note/
> local_search_key`，可撤销）；桌面 Code 行与其终端副本合并（`merged_into`，旧偏好迁移到桌面行）；
> `transcript_bytes` 与按大小排序；阅读器 Markdown 渲染（`Core/Display/Markdown` 解析器 + FlowDocument）、
> 图片缩略图（`TranscriptReader(keepImages)` 仅阅读器开启，单张 ≤ 12 MB、每次读取 ≤ 256 MB，网络图片不下载）、
> `ReadTail` 支撑的「最新」先显示末尾；全局热键（依次尝试 Ctrl+Alt+Space / Alt+Shift+Space / Ctrl+Alt+K）与
> 托盘（Shell_NotifyIcon）；浅色主题（启动时按 Windows 应用模式或用户选择改写同名画刷）；独立安装版 Claude 与
> `OpenAI.ChatGPT-Desktop` 包的识别（`Discovery/InstallVariants`，公司名 / 发布者 ID 校验，应用 Id 读包清单）。
>
> **0.7.0（2026-09-23）说明**：流畅度与易用性，读写边界不变。应用探测改用 `GetPackagesByPackageFamily` /
> `GetPackagePathByFullName`（PowerShell 仅作兜底）；增量同步：`CliProbeCache`（按长度与修改时间复用文件头
> 探测结果）、`UpsertRange` 只写有变化的行并只在有变化时递增 revision、`SyncResult.RowsChanged/RowsRemoved`、
> 子代理计数 5 分钟内复用；界面在切回窗口与每分钟自动静默同步，等用户停下输入再刷新；
> `TranscriptReader.LastExchange` / `RecentDialogue` 从文件末尾按 128 KB → 1 MB → 8 MB 逐级放宽读取，供详情面板
> 的「最近对话」使用。
>
> **0.6.0（2026-09-23）说明**：排序与兼容性，读写边界不变。新增：服务端排序（`SortField` / `SortOrder`，
> 通用多键 keyset 游标，游标指纹含排序；注册 SQLite 排序规则 `UI`：zh-CN 拼音、忽略大小写 / 全半角，
> 再以序数比较打破平局）、按类别分段与 `SectionCounts`、`OrderingRules`（应用内状态与来源次序，与 SQL
> 一致）；对话记录窗口不再以主窗口为 Owner（`ShutdownMode=OnMainWindowClose`），二次启动经命名事件让
> 首个实例自己恢复主窗口；阅读器缩放；PerMonitorV2 清单；`isMeta` / `isCompactSummary` 折叠、
> `ClaudeCodeMarkup` 解析斜杠命令记录（显示为用户发言、参数可作标题）；同一会话 ID 在多个项目文件夹
> 各有一份时，`CliMetadataReader` 与 `TranscriptLocator` 都取最后写入的一份。
>
> **0.5.0（2026-09-23）说明**：交互与易用性打磨，读写边界不变。新增：统一深色主题（含 DWM 深色标题栏）、
> 随选中项变化的详情面板、整理操作一步撤销（`SqliteCatalog.RevertGroups`，以及用本次写入产生的
> preference_revision 反向调用 `SetVisibility`，之后被再改过的行不会被覆盖）、搜索同时匹配工作目录
> （新增派生列 `project_key`，旧库启动时回填）与会话 ID、`Count` / `CountViews`、列表自动加载与刷新时保留
> 深度和位置、`settings.json`（窗口、列宽、筛选、阅读器选项）、正文阅读器重做（后台读取、顺序续读不再每页
> 从头扫描、只看对话、对话内查找、整条导出 `TranscriptExport`）、单实例二次启动切到已开窗口的修复。
>
> **0.4.0（2026-09-23）说明**：与「会话管家（Codex++ 与 Claude）」合并。新增 Codex 来源（`CodexMetadataReader`，
> 只读 `state_5.sqlite` + rollout 文件名索引）、ChatGPT 启动（`ChatGptLauncher`：Codex++ 快捷方式优先，否则
> 商店 AUMID）、`codex resume` 继续（`CodexSessionLauncher`），正文阅读器改为同时解析 Claude 与 Codex 两种
> 格式并折叠工具内容。原会话管家通过 App Server 归档/解除归档 Codex 的链路与"待执行计划"未并入。下文的
> 能力矩阵仍只描述 Claude 来源。
>
> **0.3.1（2026-09-22）说明**：数据目录默认改为 `D:\ClaudeLauncherData`（可用 `CLAUDE_LAUNCHER_DATA_DIR`
> 覆盖，D 盘不可用时回落到 `%LOCALAPPDATA%\ClaudeSessionLauncher`），旧目录首次启动时整体迁移。
> 另新增本工具自己的分组（`GroupRules` + `local_group` 列），Claude 的侧边栏分组仍然只读。
>
> **0.3.0（2026-09-22）说明**：本工具已收窄为「外部对话记录保存器 + 官方 Claude 启动入口」。
> 所有写 Claude 数据的功能（恢复到 Claude / 从 Claude 归档 / 批量恢复 / 在 Claude 中标记已归档）
> 及其门禁代码（`NativeOperationPolicy`、`OperationStateMachine`、`DesktopRecordStore`、
> `NativeArchiveWriter`）已整体删除。下文 G02 / G03 / G08 以及 `ArchiveNative` / `RestoreNative`
> 各行是 0.2.0 的历史记录，仅供追溯，不再对应任何现有功能。

# 能力矩阵

观测日期：2026-09-21　观测版本：Claude `2.2553.1.0`　启动器版本：0.1.0

能力绑定到具体的 Claude 版本。版本一变，所有 `Verified` 自动降级为 `VersionMismatch`，
原生写入路径关闭，官方启动与本工具视图归档不受影响（见 `BackendCapabilities.For`）。

## 取值

| 值 | 含义 |
|---|---|
| `Verified` | 在上述版本上实测通过 |
| `Unknown` | 没有证据。**不是**"大概可以"，代码中一律按拒绝处理 |
| `Unsupported` | 该后端结构上不存在此概念 |
| `VersionMismatch` | 有记录，但版本对不上 |

## 矩阵

| 操作 | Desktop Code | CLI Code | Chat | Cowork |
|---|---|---|---|---|
| `Discover` | Verified | Verified | Unknown | Unknown |
| `EnumerateMetadata` | Verified | Verified | Unknown | Unknown |
| `OpenApp` | Verified | Unknown | Unknown | Unknown |
| `OpenConversation` | Unknown | Unknown | Unknown | Unknown |
| `ArchiveNative` | **Unknown** | **Unsupported** | Unknown | Unknown |
| `RestoreNative` | **Unknown** | **Unsupported** | Unknown | Unknown |
| `ReadBackNativeState` | Verified | Verified | Unknown | Unknown |
| `DetectActive` | Unknown | Unknown | Unknown | Unknown |
| `EnumerateAffectedSet` | Unknown | Unknown | Unknown | Unknown |

CLI 的 `ArchiveNative` 是 `Unsupported` 而非 `Unknown`：CLI transcript 根本没有归档状态这个概念，
写成 Unknown 会暗示"也许存在"。

## 本机实测数据（2026-09-21）

| 项 | 实测 | 设计文档基线（2026-09-21 规划时） |
|---|---|---|
| Desktop Code 会话记录 | 7 条（6 活跃 / 1 归档） | 7 条（6/1）✅ 一致 |
| CLI 顶层主会话 | **39–42 条** | 133 条 |
| 子代理 JSONL | 1,550 个 | 1,683 个 |
| Desktop↔CLI 已验证关联 | **7 条** | 4 条 |
| 未解析来源 ID | **0 条** | 3 条 |
| `CLAUDE_CONFIG_DIR` | **`D:\ClaudeData`（环境变量）** | 默认 `~/.claude` |

**CLI 数量从 133 降到 39–42 的原因**：同日早些时候用户清理了 98 条已归档的 transcript 原件，
并把配置目录迁到了 D 盘。**不是数据丢失**，原件在 `C:\Users\<用户名>\.claude` 备份中仍可恢复。
按设计文档要求，此处记录实测值而非沿用规划期的旧数值。

`0 条未解析`优于规划期基线，因为应用当日重建过会话记录，全部指向现存 transcript。

## 目录覆盖率对账（2026-09-21 实测）

2026-09-22 用户执行 `restore_pruned.ps1` 之后：

| 历史池 | 顶层会话 | 是否收录 |
|---|---|---|
| `D:\ClaudeData\projects`（活动 `CLAUDE_CONFIG_DIR`） | **139** | ✅ 全部 |
| `C:\Users\<用户名>\.claude\projects`（迁移前副本） | 133 | ✅ 其内容已全在活动根，判定为**冗余副本**，缺失 0 |
| `D:\ClaudeArchive`（Markdown 归档） | 100 | ❌ 非会话格式，未建索引（原件已恢复，此处仅作文字备份） |
| Desktop Code store | 7 | ✅ 全部 |
| Chat / Cowork | 0 | ❌ 能力 `Unknown` |

**本机 CLI 与 Desktop 历史已 100% 收录**（139 唯一会话，Desktop 7 条全部与 CLI 关联）。

恢复前的快照为：活动根 42 条、C 盘副本 133 条、覆盖约 31%，差额 97 条是同日
`prune_archived.ps1` 删除原件、仅存于备份的会话。

`AlternateRootProbe` 比对**会话 ID** 而非文件数，因此能区分"另一个根里有你没有的会话"
（报 `UnknownSource` + 缺失条数）与"冗余副本"（报 `Resolved` + 缺失 0）。两者混同会让
一份完整的目录看起来还差一百多条。

设计上只扫描**一个**已验证的账户/来源集合，因此多根不合并：另一个根里的会话未必能从当前
配置恢复，收录进来会让列表看似完整、实际不可用。`AlternateRootProbe` 负责把这类根探测出来
并附路径与条数，使"未覆盖"可见而非静默。

只要存在非 `Resolved` 的来源，`globalUniqueConversationCount` 即为 `null` 并附原因，
不输出未经证明的全局总数。

## 门禁状态

| 门禁 | 状态 | 说明 |
|---|---|---|
| G01 官方启动 | **部分通过** | 包身份与 AUMID 已确认，激活已实现；"官方窗口可交互"未自动断言 |
| G02 Desktop 归档/恢复 UI | **未做** | 需在官方界面用可丢弃会话实测 |
| G03 worktree / 级联保护 | **未做** | 保护快照与影响集合枚举均未实现 |
| G04 Chat 能力 | **未做** | 本机无可验证来源 |
| G05 Cowork 能力 | **未做** | 不读写 `local-agent-mode-sessions` |
| G06 性能因果 | **未通过** | L1 p95 1522 ms > 1500 ms；L2/L3 未测 |
| G07 开发工具 | **通过** | .NET SDK 10.0.401 经 winget 官方源安装 |
| G08 会话记录写入（恢复到 Claude / 从 Claude 归档） | **测试通过，真实写入待执行** | `DesktopRecordStore`：21 项集成测试；对真实数据 dry-run 72/72 可写、0 推断；首次真实写入需 Claude 退出，尚未发生 |

## G08 细节（2026-09-22）

记录形状取自 Claude 自己写的 64 条记录中字段最少的一条（14 字段），以及 `app.asar` 里
`finishRegisterExternalSession` 写出的导入记录（11 字段）。两者交集加上 `model / titleSource /
permissionMode`，就是本工具生成的记录。

已验证（对本机真实数据）：
- 启动目录规则「文件里任一 cwd 的 slug == 项目目录名」136/139；3 个例外是 `D:\` / `G:\` 联接同一目录，
  且它们**已有的桌面记录 cwd 也写的 G:\**，证明 App 容忍这种不一致
- 账号/组织目录唯一（1 个）；`bridge-session` 里的 owner 与之一致
- 侧边栏分组分配（41 条）没有任何一条指向仅终端会话——**恢复记录不会自动回到分组**，需要在 Claude 里重新拖

未验证：
- 真实写入后 Claude 是否正常列出并能 `--resume`（需 Claude 退出后由用户执行一次）
- 恢复带 `bridge-session`（有服务端记录）的 15 条会话时，App 是否会与服务端状态冲突
