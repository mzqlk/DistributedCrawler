import numpy as np
import streamlit as st
import pyLDAvis
import pyLDAvis.gensim_models as gensimvis
from pyLDAvis._prepare import js_PCoA
import plotly.express as px
from wordcloud import WordCloud
import matplotlib.pyplot as plt
import pandas as pd


class Visualizer:
    @staticmethod
    def render_topic_trend(df: pd.DataFrame):
        """渲染主题趋势折线图（过滤空年份）"""
        df_valid = df.dropna(subset=['year']).copy()
        if df_valid.empty:
            st.warning("无有效年份数据，无法展示趋势图")
            return

        trend_data = df_valid.groupby(['year', 'topic_label']).size().reset_index(name='counts')
        trend_data['year'] = trend_data['year'].astype(int)

        fig = px.line(
            trend_data,
            x='year',
            y='counts',
            color='topic_label',
            title='Research Topic Trends Over Years',
            markers=True,
            labels={'counts': 'Number of Papers', 'year': 'Publication Year'}
        )
        fig.update_layout(xaxis_tickmode='linear')
        st.plotly_chart(fig, use_container_width=True)

    @staticmethod
    def render_wordcloud(lda_model, topic_id: int):
        """渲染指定主题的词云（无中文字体依赖）"""
        topic_words = dict(lda_model.show_topic(topic_id, topn=30))

        wc = WordCloud(
            background_color='white',
            width=800,
            height=400,
            colormap='viridis',
            max_font_size=100,
            min_font_size=10
        )
        wc.generate_from_frequencies(topic_words)

        fig, ax = plt.subplots(figsize=(10, 5))
        ax.imshow(wc, interpolation='bilinear')
        ax.axis('off')
        st.pyplot(fig)

    @staticmethod
    def render_topic_distribution(df: pd.DataFrame):
        """渲染主题分布柱状图"""
        dist_data = df['topic_label'].value_counts().reset_index()
        dist_data.columns = ['Topic', 'Number of Papers']

        fig = px.bar(
            dist_data,
            x='Topic',
            y='Number of Papers',
            title='Paper Distribution by Topic',
            color='Number of Papers',
            color_continuous_scale='viridis'
        )
        st.plotly_chart(fig, use_container_width=True)

    @staticmethod
    def render_topic_intertopic_map(lda_model, corpus, dictionary):
        """
        使用 pyLDAvis 生成主题间距离图与词-主题相关性交互全景，
        嵌入 Streamlit 页面中。
        """
        # 准备可视化数据
        # 仍使用 pyLDAvis 默认的 PCoA 降维；NumPy 2.x 下其结果为虚部恒为 0 的复数，
        # 会导致 JSON 序列化失败，这里只取实部
        vis_data = gensimvis.prepare(
            lda_model, corpus, dictionary, sort_topics=False,
            mds=lambda dists: np.real(js_PCoA(dists))
        )
        # 生成完整 HTML
        html_string = pyLDAvis.prepared_data_to_html(vis_data)
        # 以组件形式嵌入 Streamlit，设置合适宽高
        st.components.v1.html(html_string, width=1100, height=800, scrolling=True)
