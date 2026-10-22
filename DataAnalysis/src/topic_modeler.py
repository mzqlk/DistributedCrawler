from typing import Dict, List
import gensim
from gensim import corpora
from gensim.models import CoherenceModel
import pandas as pd
import os
import json
from config import DATA_PROCESSED_DIR, LDA_WORDS_PER_TOPIC


class TopicModeler:
    def __init__(self):
        self.model_path = os.path.join(DATA_PROCESSED_DIR, "lda_model")
        self.dict_path = os.path.join(DATA_PROCESSED_DIR, "dictionary")
        self.corpus_path = os.path.join(DATA_PROCESSED_DIR, "corpus.mm")

    def _safe_to_list(self, text) -> List[str]:
        """类型转换函数：确保输入是 List[str]"""
        if isinstance(text, list):
            return text
        if isinstance(text, str):
            try:
                parsed = json.loads(text)
                if isinstance(parsed, list):
                    return parsed
            except:
                pass
            return text.split()
        return []

    def train(self, df: pd.DataFrame, num_topics: int, passes: int, alpha: str, eta: str):
        """训练LDA主题模型并计算评估指标"""
        raw_texts = df['tokens'].tolist()
        texts = [self._safe_to_list(t) for t in raw_texts]

        if not texts or all(len(t) == 0 for t in texts):
            raise ValueError("无有效文本语料用于训练模型，请检查 tokens 列是否正确生成")

        # 构建词典和语料库
        dictionary = corpora.Dictionary(texts)
        dictionary.filter_extremes(no_below=2, no_above=0.8)
        if len(dictionary) < 10:
            raise ValueError("有效词汇量过少（<10），请增加论文数据量")
        corpus = [dictionary.doc2bow(text) for text in texts]

        # 训练LDA模型 (使用传入的参数)
        lda_model = gensim.models.LdaMulticore(
            corpus=corpus,
            id2word=dictionary,
            num_topics=num_topics,
            random_state=100,
            passes=passes,
            alpha=alpha,
            eta=eta,
            workers=2,
            per_word_topics=False
        )

        # 保存模型
        lda_model.save(self.model_path)
        dictionary.save(self.dict_path)
        corpora.MmCorpus.serialize(self.corpus_path, corpus)

        perplexity = lda_model.log_perplexity(corpus)  # 困惑度 (越小越好)

        # 主题一致性 (C_V 得分，范围 0-1，越大越好)
        coherence_model_lda = CoherenceModel(
            model=lda_model,
            texts=texts,
            dictionary=dictionary,
            coherence='c_v',
            window_size=50,
            processes=8
        )
        coherence = coherence_model_lda.get_coherence()

        return lda_model, dictionary, corpus, perplexity, coherence

    def infer_topics_for_df(self, df: pd.DataFrame, lda_model, dictionary) -> pd.DataFrame:
        """为每篇论文推断最相关的主题"""
        raw_texts = df['tokens'].tolist()
        texts = [self._safe_to_list(t) for t in raw_texts]
        corpus = [dictionary.doc2bow(text) for text in texts]

        dominant_topics = []
        topic_probs = []
        for bow in corpus:
            topics = sorted(lda_model.get_document_topics(bow, minimum_probability=0.0), key=lambda x: -x[1])
            dominant_topics.append(topics[0][0] if topics else -1)
            topic_probs.append(round(topics[0][1], 3) if topics else 0.0)

        df['topic_id'] = dominant_topics
        df['topic_confidence'] = topic_probs
        return df

    def get_topic_labels(self, lda_model, num_words: int) -> Dict[int, str]:
        """获取主题的Top3关键词作为标签"""
        labels = {}
        for idx, topic in lda_model.print_topics(-1, num_words=num_words):
            words = [w.split('*')[1].replace('"', '').strip() for w in topic.split('+')]
            labels[idx] = f"Topic {idx+1}: {', '.join(words[:3])}"
        return labels
