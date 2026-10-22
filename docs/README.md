# docs 索引

本目录整理自 `D:\毕设相关`（毕业设计相关材料，2026-10-22 整理），统一转成 Markdown，并对照仓库代码做了核对。**代码与文档冲突时以代码为准**，已知差异见 [`project/overview.md` §5](project/overview.md#5-文档与代码的差异以代码为准)。

## 先读这个

| 场景 | 读哪里 |
|---|---|
| 了解项目是什么、做到哪一步、有哪些已知问题 | [`project/overview.md`](project/overview.md) |
| 接下来做什么、按什么规则推进、做过哪些决定 | [`roadmap.md`](roadmap.md) |
| 修复 Master 问题前查看审阅结论（编号 M-xx、触发场景、修复建议） | [`review/master.md`](review/master.md) |
| 改动通信/调度/引擎/插件/UI 前了解设计约定 | [`project/design.md`](project/design.md) |
| 运行、Docker、端口、环境变量、配置项 | [`project/deployment.md`](project/deployment.md) |
| 原 16 周计划里还有哪些没做（流程设计器、插件 UI、压测、文档…） | [`project/dev-plan.md`](project/dev-plan.md) |
| 数据分析模块（Python/Streamlit/LDA）、导出 JSON 的格式契约、实验数据 | [`analysis/README.md`](analysis/README.md) |
| 毕业论文正文、插图、格式规范 | [`thesis/README.md`](thesis/README.md) |
| 开题时规划但未实现的功能（需求池） | [`thesis/proposal.md`](thesis/proposal.md) |

## 目录结构

```text
docs/
├── README.md                    本索引
├── roadmap.md                   后续任务（T1–T10）、管理会话执行规则、决策规则与决策记录
├── project/
│   ├── overview.md              定位、解决方案结构、完成度、已知问题、文档与代码差异
│   ├── design.md                架构、流程、proto、双向流可靠性、各服务与组件要点、未落地设计
│   ├── dev-plan.md              16 周开发计划精简版 + 完成状态
│   └── deployment.md            运行/部署命令、环境变量、端口、配置项
├── review/
│   └── master.md                Master 端代码审阅报告（T2）：按 P0–P3 排序的问题清单（M-01 起编号）
├── analysis/
│   ├── README.md                数据分析模块说明、输入 JSON 契约、流程、实验结果、性能
│   ├── sample-export.json       Master 导出 JSON 示例（2 条）
│   └── reference-flask-app/     早期 Flask 版分析程序（作者合作网络、热点预测）的功能地图（源码只在本地）
└── thesis/
    ├── README.md                论文元信息、章节速查、插图索引、评审意见、过程材料、格式规范
    ├── thesis.md                论文终稿全文
    ├── proposal.md              任务书 + 开题报告要点（原计划 vs 实现）
    ├── defense-slides.md        答辩 PPT 要点与讲稿
    └── figures/                 论文 27 张插图（fig{章}-{序}.png）
```

## 来源对照（`D:\毕设相关\`）

| 原文件 | 整理到 |
|---|---|
| `项目文档.docx` | `project/design.md`、`project/deployment.md`（其中的代码片段多为早期快照或未实现的设计稿，未复制） |
| `项目结构.docx` | `project/overview.md` §2（实体/接口源码以仓库为准，未复制） |
| `开发计划（16周）.docx` | `project/dev-plan.md` |
| `部署命令.docx` | `project/deployment.md` |
| `代码.docx`（Python 数据分析模块源码） | 仓库根目录 `DataAnalysis/`（说明在 `analysis/README.md`） |
| `数据分析\元数据\*.json`、`数据分析结果\*.csv`、`*.xlsx` | `analysis/README.md`（统计与指标）、`analysis/sample-export.json`；原始数据未复制（约 7 MB） |
| `数据分析\基础代码\…\PythonApplication1.py` | `analysis/reference-flask-app/README.md`（功能地图；源码来源不确定，不放进公开仓库） |
| `数据分析\25BTVis….pdf`、`基于大语言模型的….docx` | `analysis/README.md` 参考文献摘要 |
| 论文 `.docx` / `.pptx`、任务书、开题报告、各类表格 | `thesis/` |
| 学院模板、撰写规范、装订通知 | `thesis/README.md` 末节 |
| 查重/AIGC 报告、早期草稿副本、文献综述与外文翻译全文、各类评语表 | 未整理，也不放进公开仓库 |
