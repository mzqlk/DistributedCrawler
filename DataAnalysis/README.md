# DataAnalysis：学术文献主题分析（Streamlit + LDA）

读取 Master「数据查看」页导出的 `任务数据_*.json`，完成英文文本预处理 → LDA 主题建模（困惑度 + C_V 一致性）→ 主题分布 / 词云 / 年度趋势 / pyLDAvis 主题距离图 → CSV 导出。

```bash
cd DataAnalysis
python -m venv .venv
.venv\Scripts\activate          # Linux/macOS: source .venv/bin/activate
pip install -r requirements.txt
streamlit run app.py
```

浏览器打开后：侧边栏上传一个或多个导出 JSON → 设置主题数等参数 → 训练 → 查看图表、筛选、导出 CSV。

- 首次运行会把 NLTK 资源（stopwords、punkt_tab、wordnet、averaged_perceptron_tagger_eng）下载到 `data/processed/nltk_data`；上传文件、SQLite、模型文件都写在 `data/`（已加入 `.gitignore`）。
- **开着代理时 NLTK 下载会失败**（报 `Security Violation [pathsec.urlopen]: refusing a proxied fetch`）：NLTK 3.10 默认拒绝经代理下载。确认代理可信后，在启动前设置环境变量 `NLTK_ALLOW_PROXIED_URLOPEN=1`（PowerShell：`$env:NLTK_ALLOW_PROXIED_URLOPEN=1`），或关闭代理后首次运行一次。
- 依赖版本已锁定为实测通过的组合（Python 3.12）。
- 输入 JSON 的字段约定、处理流程、实验结果见 [`docs/analysis/README.md`](../docs/analysis/README.md)。修改 `ThesisPlugin` 的输出字段时需同步修改 `src/data_processor.py`。
