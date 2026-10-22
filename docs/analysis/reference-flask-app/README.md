# 参考实现：Flask 版学术分析程序（“数模7.0”）

> 源码**未放入公开仓库**（来源不确定），只保留功能说明。本地位置：`D:\毕设相关\数据分析\基础代码\数模7.0\PythonApplication1\PythonApplication1.py`（2025-04，Visual Studio Python 项目，约 1430 行，含 `templates/`、`static/`）。它是数据分析模块的“基础代码”，与参考论文《基于大语言模型的学术论文热点可视综述与合作网络挖掘》的方案对应。下文行号均指该本地文件。`D:\毕设相关\数据分析\数模7.0\` 下有一份略旧的版本（1420 行），以及 `output/`（词云 PNG、关系图 HTML）、`output2/`（热点趋势 PNG）示例输出。
>
> 它**自带爬虫**（直接请求 arXiv 搜索页），不读取本仓库导出的 JSON；若要复用其中的算法，需要改成读取 `parsedData`（见 `../README.md` 的数据契约）。

## 依赖

Flask、flask-session、aiohttp、BeautifulSoup、NLTK（stopwords/wordnet/punkt/punkt_tab/averaged_perceptron_tagger）、rake-nltk、gensim、scikit-learn、PyTorch、torch-geometric、sentence-transformers（`all-MiniLM-L6-v2`，代码里写死了本机 HuggingFace 缓存路径 `LOCAL_MODEL_PATH`）、wordcloud、matplotlib、seaborn、plotly。

## 功能地图

| 路由 / 函数 | 行号（约） | 作用 |
|---|---|---|
| `POST /search` → `search()` | 84 | 主入口：查询词 → 抓 arXiv → 取前 5 篇 → LDA(5 主题) → 词云 → 作者推荐 → 合作关系图 → 热点预测 → 关键词趋势图，返回 JSON |
| `async_search` / `fetch_page` | 370 | aiohttp 并发抓 `arxiv.org/search/?searchtype=all&size=50&start=…`（信号量 10，重试 3 次），BeautifulSoup 解析 `li.arxiv-result` 的标题/作者/摘要/提交日期/链接 |
| `preprocess_english` | 202 | 去非字母数字 → 分词 → 去停用词、长度 ≤3 → 词形还原（不带词性） |
| `train_lda` | 219 | `gensim.models.LdaModel(passes=15, random_state=42)` |
| `analyze_topic_trends` | 241 | 每篇文档的主题分布按年份取均值 |
| `predict_future_topics` | 258 | 对每个主题用 1→16→1 的小 MLP（Adam，1000 轮）拟合“年份→主题强度”，外推未来 3 年；预测值高于历史均值的记为未来热点 |
| `visualize_future_topics` / `generate_annual_comparison` | 310 / 340 | 预测可视化、年度主题对比（Plotly HTML） |
| `generate_wordcloud` / `filter_word_freq` / `get_wordcloud_params` | 438–486 | 词云，过滤查询词本身，多种风格 |
| `recommend_authors` | 552 | 每位作者的摘要拼接 → TF-IDF → 余弦相似度 → 推荐 5 位相似作者；RAKE 提取研究兴趣 |
| `build_author_relationships` | 605 | 共同署名 → 合作者集合；发文量 Top5 作者 |
| `generate_relationship_graph` | 626 | 作者-作者、作者-论文异构图；论文节点特征 = Sentence-BERT 摘要嵌入；两层 `GCNConv` 得到节点表示；输出 D3.js 力导向图 HTML |
| `generate_keyword_trend_chart` / `merge_keyword_trends` | 1016 / 1130 | 关键词年度趋势曲线及多条曲线合并 |
| `GET /analyze_hot_topics`、`/hot-topics`、`/api/hot-topics`、`/merge-charts` | 1213–1384 | 热点分析页面与 API |
| `GET /relationship-graph` | 1188 | 合作关系图页面（papers 存在 flask session） |

## 可迁移到本项目的点

- **作者合作网络**：`build_author_relationships` 的共现建图逻辑可以直接用 `parsedData.AuthorsList`；可视化可换成 pyvis / Plotly network 嵌入 Streamlit。
- **趋势预测**：MLP 外推只适合多年份数据；当前采集数据全是 2026 年，需先解决数据跨度问题。
- **作者推荐**：TF-IDF + 余弦相似度，轻量，可直接移植。
