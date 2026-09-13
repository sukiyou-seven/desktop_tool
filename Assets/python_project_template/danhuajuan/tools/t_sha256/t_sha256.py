"""
@File   :   t_sha256.py
@Date   :   2024/5/28 13:48
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import hashlib


class TSHA256:
    def __init__(self, data):
        sha256 = hashlib.sha256()
        sha256.update(data.encode('utf-8'))
        self.result = sha256.hexdigest()

    def __del__(self):
        return self.result
