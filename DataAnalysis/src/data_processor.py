import json
import os
import nltk
from nltk.corpus import stopwords, wordnet
from nltk.stem import WordNetLemmatizer
from nltk.tokenize import word_tokenize
from nltk.tag import pos_tag
import pandas as pd
import sqlite3
import string
from typing import List, Dict
from config import DATA_RAW_DIR, DATA_PROCESSED_DIR

# 强制静默初始化NLTK资源（避免Streamlit弹窗）
os.environ['NLTK_DATA'] = os.path.join(DATA_PROCESSED_DIR, "nltk_data")
os.makedirs(os.environ['NLTK_DATA'], exist_ok=True)
# nltk.data.path 在 import nltk 时就已确定，事后设置 NLTK_DATA 对当前进程无效，必须直接加入搜索路径，
# 否则下载到该目录的资源找不到（只有全局 nltk_data 里恰好有资源的机器才能运行）
if os.environ['NLTK_DATA'] not in nltk.data.path:
    nltk.data.path.insert(0, os.environ['NLTK_DATA'])

# NLTK >= 3.9 起 word_tokenize / pos_tag 使用 punkt_tab / averaged_perceptron_tagger_eng
NLTK_RESOURCES = {
    'stopwords': ['corpora/stopwords'],
    'punkt_tab': ['tokenizers/punkt_tab'],
    'wordnet': ['corpora/wordnet', 'corpora/wordnet.zip'],
    'averaged_perceptron_tagger_eng': ['taggers/averaged_perceptron_tagger_eng'],
}
for package, resource_paths in NLTK_RESOURCES.items():
    for resource_path in resource_paths:
        try:
            nltk.data.find(resource_path)
            break
        except LookupError:
            continue
    else:
        nltk.download(package, download_dir=os.environ['NLTK_DATA'], quiet=True)


class DataProcessor:
    def __init__(self):
        self.stop_words = set(stopwords.words('english'))
        self.lemmatizer = WordNetLemmatizer()
        self.db_path = os.path.join(DATA_PROCESSED_DIR, "papers.db")
        os.makedirs(DATA_PROCESSED_DIR, exist_ok=True)

    def _get_wordnet_pos(self, tag: str) -> str:
        """简化POS标签映射（仅处理名词/动词/形容词/副词，提升主题质量）"""
        if tag.startswith('J'):
            return wordnet.ADJ
        elif tag.startswith('V'):
            return wordnet.VERB
        elif tag.startswith('N'):
            return wordnet.NOUN
        elif tag.startswith('R'):
            return wordnet.ADV
        else:
            return wordnet.NOUN  # 默认按名词处理

    def parse_and_clean(self, json_file_path: str) -> pd.DataFrame:
        """匹配JSON结构的解析+英文文本清洗"""
        try:
            with open(json_file_path, 'r', encoding='utf-8') as f:
                raw_data = json.load(f)
        except Exception as e:
            raise ValueError(f"JSON文件解析失败: {str(e)}")

        page_results = raw_data.get("pageResults", [])
        print(f"[DEBUG] 读取到pageResults条数: {len(page_results)}")  # 调试日志

        papers_list = []
        for page_result in page_results:
            parsed_data = page_result.get("parsedData", {})
            if not parsed_data:
                continue

            try:
                authors_list = json.loads(parsed_data.get("AuthorsList", "[]"))
            except:
                authors_list = []
            try:
                subjects_list = json.loads(parsed_data.get("SubjectsList", "[]"))
            except:
                subjects_list = []

            paper_info = {
                "arxiv_id": parsed_data.get("ArXivId", ""),
                "title": parsed_data.get("Title", "").strip(),
                "abstract": parsed_data.get("Abstract", "").strip(),
                "authors": parsed_data.get("Authors", ""),
                "authors_list": authors_list,
                "subjects": parsed_data.get("Subjects", ""),
                "subjects_list": subjects_list,
                "publication_date": parsed_data.get("PublicationDate", ""),
                "doi": parsed_data.get("DOI", ""),
                "pdf_url": parsed_data.get("PdfUrl", ""),
                "source_url": parsed_data.get("SourceUrl", "")
            }
            papers_list.append(paper_info)

        print(f"[DEBUG] 成功解析到论文条数: {len(papers_list)}")  # 调试日志
        if not papers_list:
            raise ValueError("JSON文件中未找到有效的arXiv论文数据，请检查JSON键名是否匹配")

        df = pd.DataFrame(papers_list)

        df['year'] = pd.to_datetime(df['publication_date'], errors='coerce').dt.year

        df['full_text'] = df.apply(
            lambda row: f"{row['title']} {row['abstract'] if row['abstract'] not in ['', 'this https URL'] else ''} {row['subjects']}",
            axis=1
        )

        df['tokens'] = df['full_text'].apply(self._process_english_text)

        for idx, val in df['tokens'].items():
            if not isinstance(val, list):
                print(f"[ERROR] 索引 {idx} 的 tokens 类型错误: {type(val)}, 值: {val}")
                df.at[idx, 'tokens'] = []  # 置空或修复

        df = df[df['tokens'].apply(len) >= 2].reset_index(drop=True)
        print(f"[DEBUG] 清洗后剩余有效论文条数: {len(df)}")  # 调试日志

        if df.empty:
            raise ValueError("清洗后无有效文本数据，请检查论文的标题/摘要/主题字段是否有有效内容")

        return df

    def _process_english_text(self, text: str) -> List[str]:
        # 转小写
        text = text.lower()
        # 去除标点和数字
        text = text.translate(str.maketrans('', '', string.punctuation + string.digits))
        # Tokenize + POS标注
        tokens = word_tokenize(text)
        tagged_tokens = pos_tag(tokens)
        # 词形还原 + 去停用词 + 过滤短词
        tokens = [
            self.lemmatizer.lemmatize(w, pos=self._get_wordnet_pos(tag))
            for w, tag in tagged_tokens
            if w.isalpha() and w not in self.stop_words and len(w) > 2
        ]
        return tokens

    def save_to_db(self, df: pd.DataFrame):
        conn = sqlite3.connect(self.db_path)
        # 将List转为JSON字符串存储（SQLite原生不支持List）
        df['tokens'] = df['tokens'].apply(json.dumps)
        df['authors_list'] = df['authors_list'].apply(json.dumps)
        df['subjects_list'] = df['subjects_list'].apply(json.dumps)
        df.to_sql("papers", conn, if_exists="replace", index=False)
        conn.close()

    def load_from_db(self) -> pd.DataFrame:
        conn = sqlite3.connect(self.db_path)
        df = pd.read_sql("SELECT * FROM papers", conn)
        # 还原JSON字符串为List
        df['tokens'] = df['tokens'].apply(json.loads)
        df['authors_list'] = df['authors_list'].apply(json.loads)
        df['subjects_list'] = df['subjects_list'].apply(json.loads)
        conn.close()
        return df
