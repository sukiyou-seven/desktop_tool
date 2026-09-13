"""
@File   :   redisa.py
@Date   :   2024/5/28 13:41
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import json

import redis
from flask import current_app

from danhuajuan.config import ReadConfig


class Redisa:
    def __init__(self):
        config = ReadConfig('app').data
        config = config["REDIS"]
        redis_host = config["REDIS_HOST"]
        redis_port = config["REDIS_PORT"]
        redis_password = config["REDIS_PASSWORD"]
        db = config["REDIS_DB"]
        self.timeout = config["REDIS_CACHE_TIMEOUT"]

        # 创建一个Redis连接
        # self.redis = redis.StrictRedis(host=redis_host, port=redis_port, db=db,
        #                                decode_responses=True)

        self.redis = redis.StrictRedis(host=redis_host, port=redis_port, db=db,password=redis_password,
                                       decode_responses=True)

    @staticmethod
    def is_valid_json(value):
        try:
            # 尝试反序列化字符串
            json.loads(value)
        except (TypeError, json.JSONDecodeError):
            # 如果发生TypeError或JSONDecodeError异常，说明字符串不是有效的JSON
            return False
        return True

    def set(self, key, value, expiration=None):
        """设置缓存值"""
        # if self.is_valid_json(value):
        #     raise "must be json"
        if expiration is not None:
            self.redis.set(key, value, ex=expiration)
            return
        self.redis.set(key, value, ex=self.timeout)
        return

    def get(self, key):
        """获取缓存值"""
        try:
            return json.loads(self.redis.get(key))
        except:
            return self.redis.get(key)

    def delete(self, key):
        """删除缓存值"""
        self.redis.delete(key)

    def is_exist(self, key):
        """判断缓存值是否存在"""
        return self.redis.exists(key)

    def update_json(self, key, value, expiration=60):
        """更新缓存值"""
        res = self.get(key)
        values = res.update(value)
        expiration_old = self.key_expire(key)
        if expiration_old > expiration:
            expiration = expiration_old
        self.redis.set(key, values, ex=expiration)

    def get_all(self):
        """获取所有缓存值"""
        return self.redis.keys()

    def key_expire(self, key):
        """获取缓存过期时间"""
        return self.redis.ttl(key)

    def persist(self, key):
        return self.redis.persist(key)