"""
@File   :   mongodba.py
@Date   :   2024/6/8 19:28
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
from flask import current_app


class Database_Mongo:
    def __init__(self):
        self.mongo = current_app.config['MONGO']
