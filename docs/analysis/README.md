# 数据分析模块（Python / Streamlit / LDA）

> 毕业论文中的第二个模块，独立于 C# 解决方案。源码原本只以 Word 形式存在于 `D:\毕设相关\代码.docx`，2026-10-22 还原并纳入仓库 [`DataAnalysis/`](../../DataAnalysis/)。设计说明见论文 3.2、4.2、5.2 节（[`../thesis/thesis.md`](../thesis/thesis.md)）。
>
> 还原时相对 `代码.docx` 的改动（其余逻辑未动）：
> 1. 修复粘贴丢失的换行。
> 2. NLTK 资源：改用新版名称 `punkt_tab` / `averaged_perceptron_tagger_eng`，逐项检查缺失再下载，并把项目内的 `nltk_data` 目录加入 `nltk.data.path`。原代码只在全局 `%APPDATA%\nltk_data` 已有资源的机器上能运行。
> 3. pyLDAvis 主题距离图：NumPy 2.x 下默认 PCoA 返回虚部为 0 的复数，导致 JSON 序列化失败。仍用 PCoA，只取实部。
> 4. 依赖版本锁定为实测通过的组合。
>
> 验证（2026-10-22）：在屏蔽全局 NLTK 资源的“新机器”条件下，用 22 个真实导出文件跑通 预处理 → K=8 训练 → 推断 → SQLite 读写 → 四种图表。共 1056 篇，预处理 8.4s，训练 25.9s，Log Perplexity −7.769，C_V 0.289。另外用 Streamlit AppTest 执行 `app.py` 无异常，服务能正常启动。上传文件这一步的界面交互没有自动化测试。

## 运行

```bash
cd DataAnalysis
python -m venv .venv && .venv\Scripts\activate   # 可选
pip install -r requirements.txt
streamlit run app.py
```

首次运行会把 NLTK 资源静默下载到 `data/processed/nltk_data`。运行产物：`data/raw/`（上传的 JSON）、`data/processed/papers.db`（SQLite 表 `papers`）、`lda_model`、`dictionary`、`corpus.mm`。

## 目录

| 文件 | 内容 |
|---|---|
| `app.py` | Streamlit 交互层：上传 → LDA 参数 → 指标 → 导出 → 筛选 + 图表；`st.session_state` 管理 13 个状态键 |
| `config.py` | `DATA_RAW_DIR`、`DATA_PROCESSED_DIR`、`LDA_NUM_TOPICS=8`、`LDA_WORDS_PER_TOPIC=10` |
| `src/data_processor.py` | `DataProcessor`：解析导出 JSON、英文预处理、SQLite 存取 |
| `src/topic_modeler.py` | `TopicModeler`：Gensim `LdaMulticore` 训练、困惑度 + C_V 一致性、主导主题推断、主题标签 |
| `src/visualizer.py` | `Visualizer`：主题分布柱状图、词云、年度趋势折线、pyLDAvis 主题距离图 |

## 输入数据契约（Crawler 导出 JSON）

Master“数据查看”页导出，文件名 `任务数据_{TaskName}_{TaskId前8位}_{yyyyMMddHHmmss}.json`，camelCase。示例：[`sample-export.json`](sample-export.json)（截取 2 条）。

```jsonc
{
  "taskId": "78253d48-...", "taskName": "test6", "totalPages": 50,
  "completedAt": "2026-04-25T08:19:35", "exportTime": "2026-04-25T16:25:52+08:00",
  "pageResults": [{
    "id": 190, "url": "...", "finalUrl": "...", "statusCode": 200, "statusDescription": "成功",
    "linkCount": 59, "links": ["..."],
    "parsedData": {                       // 由 ThesisPlugin 产生，键名 PascalCase
      "ArXivId": "2604.21843", "Title": "...", "Authors": "Li Chen",
      "AuthorsList": "[\"Li Chen\"]",     // 注意：是 JSON 字符串，不是数组
      "Abstract": "...", "Subjects": "Methodology (stat.ME); Machine Learning (stat.ML)",
      "SubjectsList": "[\"...\"]",        // 同上，JSON 字符串
      "PublicationDate": "23 Apr 2026", "DOI": "10.48550/arXiv.2604.21843",
      "PdfUrl": "https://arxiv.org/pdf/2604.21843.pdf", "SourceUrl": "...",
      "NormalizedUrl": "...", "Comment": ""
    }
  }]
}
```

`parsedData` 为空（非论文页）的条目会被跳过。**修改 `ThesisPlugin` 的输出键或 Master 的导出序列化方式时，必须同步改 `DataProcessor.parse_and_clean`。**

## 处理流程（论文图 [4-11](../thesis/figures/fig4-11.png)）

1. **解析**：提取 11 个字段 → DataFrame；`year = to_datetime(publication_date).year`。
2. **文本**：`full_text = title + abstract(为空或 "this https URL" 时置空) + subjects`。
3. **预处理**：小写 → 去标点和数字 → `word_tokenize` → `pos_tag` → 按词性（J/V/N/R→WordNet）词形还原 → 去停用词、非字母、长度 ≤2 的词；token 数 <2 的文献丢弃。
4. **建模**：`Dictionary.filter_extremes(no_below=2, no_above=0.8)`（词表 <10 报错）→ BOW → `LdaMulticore(random_state=100, workers=2)`。
5. **评估**：`log_perplexity(corpus)`（越小越好）；`CoherenceModel(coherence='c_v', window_size=50)`（0–1，越大越好）。
6. **推断**：每篇取概率最高的主题 → `topic_id`、`topic_confidence`（3 位小数）；标签 `"Topic {i+1}: 前3个关键词"`。
7. **可视化与筛选**：按主题 / 年份范围 / 最小置信度联动筛选；表格中 `arxiv_id` 渲染为链接。
8. **导出**：全量或当前视图 CSV（UTF-8）。

UI 参数：K 2–15（默认 8）、passes 5–30（默认 10）、alpha `symmetric|asymmetric|auto`、eta `symmetric|auto`、每主题词数 5–30（默认 10）。

## 输出 CSV 列

`arxiv_id, title, abstract, authors, authors_list, subjects, subjects_list, publication_date, doi, pdf_url, source_url, year, full_text, tokens, topic_id, topic_confidence, topic_label`（列表列为 JSON 字符串）。

## 数据集与结果（原始文件留在 `D:\毕设相关\数据分析\`，未复制进仓库）

| 路径 | 内容 |
|---|---|
| `元数据/任务数据_*.json` | 22 个导出文件，约 6.6 MB：`Model1`(test6)、`Model3`(test8) 各 50 页；`machinelearning1..20` 各 50–51 页。共 1114 页，含论文数据约 1050 条，去重后 1011 篇 arXiv 论文 |
| `数据分析结果/arxiv_papers_with_topics_Model1_8.csv` | Model1 数据 K=8 结果，48 篇 |
| `数据分析结果/arxiv_papers_with_topics_Model1+3_8.csv` | Model1+Model3 K=8 结果，95 篇 |
| `困惑度（Perplexity） 和主题一致性（Coherence Score）验证.xlsx` | 不同 K 的指标（下表） |

注意：采集到的论文**发表年份全部是 2026**（爬的是 arXiv 近期页面），所以“年度趋势图”实际上只有一个点。想让趋势分析有意义，需要采集跨年份的数据（如 arXiv 按年份的 listing / search）。

### 主题数选择实验

论文表 5-2（1000 篇 arXiv 元数据，passes=10，对称先验，80/20 留出集算困惑度，3 次平均），据此选定 **K=9**：

| K | C_V | Log Perplexity |
|---|---|---|
| 6 | 0.38 | -7.0 |
| 7 | 0.39 | -7.1 |
| 8 | 0.41 | -7.1 |
| 9 | **0.43** | -7.1 |
| 10 | 0.38 | -7.2 |

xlsx 中的原始记录（数值低于论文表）：

| 数据集 | K=4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---|---|---|---|---|---|---|
| Model1+3（C_V / LogPPL） | — | 0.25 / -7.0 | 0.29 / -7.1 | 0.28 / -7.1 | 0.31 / -7.1 | 0.33 / -7.1 | 0.31 / -7.2 |
| machinelearning（C_V / LogPPL） | 0.28 / -7.6 | 0.30 / -7.6 | 0.29 / -7.7 | 0.29 / -7.7 | 0.28 / -7.7 | — | — |

### 性能（论文表 6-3）

| 场景 | 要求 | 实测 |
|---|---|---|
| 100 条预处理 | ≤15s | 10.38s |
| 1000 条预处理 | ≤15s | 13.48s |
| 100 条，K=5，passes=10 训练 | ≤100s | 73.30s |
| 100 条，K=10，passes=20 训练 | ≤100s | 78.53s |

## 任务书要求但未实现的分析功能

任务书（[`../thesis/proposal.md`](../thesis/proposal.md)）还要求：主题多样性指标、主题层次聚类树（参考 BTVis）、基于降维的文档散点图、**作者合作网络**。论文与答辩把它们列为展望。可复用的参考实现：

- [`reference-flask-app/`](reference-flask-app/)：早期 Flask 版分析程序（“数模7.0”），已经实现作者合作关系图（Sentence-BERT + GCN + D3.js 力导向图）、作者推荐、热点趋势预测。说明见其 README。
- 参考文献：
  - 卢文昊, 邹杭纳, 汤颖. *BTVis：基于 BERTopic 的交互式层次主题建模可视分析系统*. 计算机辅助设计与图形学学报, 2024（PDF 在 `D:\毕设相关\数据分析\25BTVis 基于BERTopic的交互式层次主题建模可视分析系统.pdf`）——任务书中“主题概览视图 / 文档分析视图”的来源。
  - 林孝泽, 王强, 沈俊辰. *基于大语言模型的学术论文热点可视综述与合作网络挖掘*. 2025 全国大学生统计建模大赛作品（`D:\毕设相关\数据分析\基于大语言模型的学术论文热点可视综述与合作网络挖掘.docx`）——TF-IDF + 预训练模型提取关键词；LDA + DBSCAN 估计主题数并聚合热点；WordCloud / 热点趋势折线；Sentence-BERT 作者语义嵌入 + 余弦相似度构建合作图谱，GNN 建模，神经网络回归预测热点，D3.js 可视化。任务书中“热点趋势视图 / 合作网络视图”的来源。
  - Röder M, Both A, Hinneburg A. *Exploring the Space of Topic Coherence Measures*. WSDM 2015——C_V 指标出处，也是毕设外文翻译的原文。
