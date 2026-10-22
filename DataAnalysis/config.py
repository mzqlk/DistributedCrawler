import os

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_RAW_DIR = os.path.join(BASE_DIR, "data", "raw")
DATA_PROCESSED_DIR = os.path.join(BASE_DIR, "data", "processed")
# LDA模型参数
LDA_NUM_TOPICS = 8
LDA_WORDS_PER_TOPIC = 10
