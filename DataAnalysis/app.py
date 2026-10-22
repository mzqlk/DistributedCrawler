import streamlit as st
import os
import pandas as pd
from config import DATA_RAW_DIR
from src.data_processor import DataProcessor
from src.topic_modeler import TopicModeler
from src.visualizer import Visualizer


def init_session_state():
    st.session_state.setdefault('processor', None)
    st.session_state.setdefault('modeler', None)
    st.session_state.setdefault('df_raw', None)
    st.session_state.setdefault('data_processed', False)
    st.session_state.setdefault('last_uploaded', None)
    st.session_state.setdefault('model_trained', False)
    st.session_state.setdefault('df', None)
    st.session_state.setdefault('lda_model', None)
    st.session_state.setdefault('dictionary', None)
    st.session_state.setdefault('topic_labels', None)
    st.session_state.setdefault('corpus', None)
    st.session_state.setdefault('perplexity', None)
    st.session_state.setdefault('coherence', None)


st.set_page_config(page_title="ArXiv Topic Visualization MVP", layout="wide", page_icon="📊")
st.title("📊 ArXiv Paper Topic Visualization (P0)")
init_session_state()

if st.session_state['processor'] is None:
    st.session_state['processor'] = DataProcessor()
if st.session_state['modeler'] is None:
    st.session_state['modeler'] = TopicModeler()

processor = st.session_state['processor']
modeler = st.session_state['modeler']

# -------------------------- 侧边栏 --------------------------
st.sidebar.header("1. Data Upload")
uploaded_files = st.sidebar.file_uploader("Upload arXiv JSON Metadata", type=['json'], accept_multiple_files=True)

if uploaded_files and len(uploaded_files) > 0:
    # 获取当前上传的文件名集合
    current_file_set = set(f.name for f in uploaded_files)

    # 检查是否是新上传的文件（或文件数量变动）
    if not st.session_state['data_processed'] or st.session_state['last_uploaded'] != current_file_set:

        os.makedirs(DATA_RAW_DIR, exist_ok=True)
        all_dfs = []
        error_occurred = False

        try:
            with st.spinner(f"Parsing and cleaning {len(uploaded_files)} file(s)..."):
                for uploaded_file in uploaded_files:
                    # 1. 保存文件
                    save_path = os.path.join(DATA_RAW_DIR, uploaded_file.name)
                    with open(save_path, "wb") as f:
                        f.write(uploaded_file.getbuffer())

                    # 2. 解析单个文件
                    df_single = processor.parse_and_clean(save_path)
                    all_dfs.append(df_single)

                # 3. 合并所有 DataFrame
                if all_dfs:
                    df_combined = pd.concat(all_dfs, ignore_index=True)
                    processor.save_to_db(df_combined)

                    st.session_state['df_raw'] = df_combined
                    st.session_state['data_processed'] = True
                    st.session_state['last_uploaded'] = current_file_set
                    # 重置模型状态
                    st.session_state['model_trained'] = False
                    st.sidebar.success(f"✅ Processed {len(all_dfs)} files successfully! Total papers: {len(df_combined)}")
                else:
                    st.sidebar.error("❌ No valid data found in uploaded files.")

        except Exception as e:
            st.sidebar.error(f"❌ Data processing failed: {str(e)}")
            st.session_state['data_processed'] = False

# -------------------------- LDA 参数控制面板 --------------------------
if st.session_state['data_processed']:
    st.sidebar.header("2. LDA Parameters")

    num_topics_input = st.sidebar.slider("Number of Topics (K)", min_value=2, max_value=15, value=8, help="主题数量")
    passes_input = st.sidebar.slider("Training Passes", min_value=5, max_value=30, value=10, help="迭代次数")
    alpha_input = st.sidebar.selectbox("Alpha (Doc-Topic Prior)", options=['symmetric', 'asymmetric', 'auto'], index=0)
    eta_input = st.sidebar.selectbox("Eta (Topic-Word Prior)", options=['symmetric', 'auto'], index=0)
    words_per_topic_input = st.sidebar.number_input("Top N Words per Topic", min_value=5, max_value=30, value=10)

    if st.sidebar.button("🚀 Train LDA Model"):
        try:
            with st.spinner("Training LDA model..."):
                df = st.session_state['df_raw']

                lda_model, dictionary, corpus, perplexity, coherence = modeler.train(
                    df,
                    num_topics=num_topics_input,
                    passes=passes_input,
                    alpha=alpha_input,
                    eta=eta_input
                )

                df = modeler.infer_topics_for_df(df, lda_model, dictionary)
                topic_labels = modeler.get_topic_labels(lda_model, words_per_topic_input)
                df['topic_label'] = df['topic_id'].map(topic_labels)
                processor.save_to_db(df)

                st.session_state['df'] = df
                st.session_state['lda_model'] = lda_model
                st.session_state['dictionary'] = dictionary
                st.session_state['corpus'] = corpus
                st.session_state['topic_labels'] = topic_labels
                st.session_state['model_trained'] = True
                st.session_state['perplexity'] = perplexity
                st.session_state['coherence'] = coherence

            st.sidebar.success("✅ Model trained successfully!")
        except Exception as e:
            st.sidebar.error(f"❌ Model training failed: {str(e)}")
            st.session_state['model_trained'] = False

# -------------------------- 模型评估指标展示区 --------------------------
if st.session_state['model_trained']:
    st.sidebar.header("3. Model Metrics")
    col1, col2 = st.sidebar.columns(2)
    col1.metric("Log Perplexity", f"{st.session_state['perplexity']:.4f}", help="越小越好")
    col2.metric("Coherence (C_V)", f"{st.session_state['coherence']:.4f}", help="越大越好 (0-1)")

# -------------------------- 数据导出区 --------------------------
if st.session_state['model_trained']:
    st.sidebar.header("4. Export Data")
    full_df = st.session_state['df']
    csv_full = full_df.to_csv(index=False).encode('utf-8')
    st.sidebar.download_button(
        label="📥 Download Full Dataset (CSV)",
        data=csv_full,
        file_name='arxiv_papers_with_topics.csv',
        mime='text/csv',
    )

# -------------------------- 主界面 --------------------------
if st.session_state['model_trained']:
    df = st.session_state['df']
    lda_model = st.session_state['lda_model']
    topic_labels = st.session_state['topic_labels']

    st.sidebar.header("5. Filters")
    selected_topics = st.sidebar.multiselect(
        "Select Topics",
        options=list(topic_labels.values()),
        default=list(topic_labels.values())
    )

    df_valid_year = df.dropna(subset=['year']).copy()
    selected_years = (None, None)
    if not df_valid_year.empty:
        min_year = int(df_valid_year['year'].min())
        max_year = int(df_valid_year['year'].max())
        if min_year == max_year:
            st.sidebar.info(f"📅 Year: {min_year}")
            selected_years = (min_year, max_year)
        else:
            selected_years = st.sidebar.slider("Year Range", min_year, max_year, (min_year, max_year))

    min_confidence = st.sidebar.slider("Min Confidence", 0.0, 1.0, 0.0, 0.05)

    filtered_df = df.copy()
    filtered_df = filtered_df[filtered_df['topic_label'].isin(selected_topics)]
    if selected_years[0] is not None:
        filtered_df = filtered_df[(filtered_df['year'] >= selected_years[0]) & (filtered_df['year'] <= selected_years[1])]
    filtered_df = filtered_df[filtered_df['topic_confidence'] >= min_confidence]

    if filtered_df.empty:
        st.warning("⚠️ No papers match the filters.")
    else:
        col_export, _ = st.columns([1, 3])
        with col_export:
            csv_filtered = filtered_df.to_csv(index=False).encode('utf-8')
            st.download_button(
                label="📥 Download Current View (CSV)",
                data=csv_filtered,
                file_name='filtered_papers.csv',
                mime='text/csv',
            )

        st.subheader("📈 Key Visualizations")
        col1, col2 = st.columns([1, 1])

        with col1:
            Visualizer.render_topic_distribution(filtered_df)

        with col2:
            st.subheader("Topic Word Cloud")
            topic_id_to_view = st.selectbox(
                "Select a topic",
                options=list(topic_labels.keys()),
                format_func=lambda x: topic_labels[x]
            )
            Visualizer.render_wordcloud(lda_model, topic_id_to_view)

        st.subheader("📊 Research Topic Trends")
        Visualizer.render_topic_trend(filtered_df)

        with st.expander("🔍 Topic Intertopic Distance Map"):
            Visualizer.render_topic_intertopic_map(
                st.session_state['lda_model'],
                st.session_state['corpus'],
                st.session_state['dictionary']
            )

        with st.expander("📄 View Filtered Paper Data"):
            display_cols = ['title', 'authors', 'year', 'topic_label', 'topic_confidence', 'arxiv_id']
            st.dataframe(
                filtered_df[display_cols].sort_values(by='topic_confidence', ascending=False),
                column_config={
                    "arxiv_id": st.column_config.LinkColumn("arXiv ID", display_text="arxiv.org/abs/{arxiv_id}")
                },
                use_container_width=True
            )
else:
    st.info("👈 Please upload data and train the model via the sidebar.")
