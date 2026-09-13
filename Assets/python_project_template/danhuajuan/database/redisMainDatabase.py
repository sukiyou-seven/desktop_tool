"""
@File   :   redisMainDatabase.py
@Date   :   2024/6/28 20:09
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp

存储方式一定是 例如一个用户一个键
            一个商品一个键
这种  所以 update 函数 无需考虑 取出的不是 dict



"""
import json

import redis
from flask import current_app


class RedisMainDatabase:
    """
    fetch_one(key)
    fetch_all()
    set()
    update()
    """

    def __init__(self, table_name):
        redis_host = current_app.config["REDIS_HOST"]
        redis_port = current_app.config["REDIS_PORT"]
        redis_password = current_app.config["REDIS_PASSWORD"]
        db = current_app.config["REDIS_DB"]
        self.timeout = current_app.config["REDIS_CACHE_TIMEOUT"]

        # 创建一个Redis连接
        self.redis_client = redis.StrictRedis(host=redis_host, port=redis_port, password=redis_password, db=db,
                                              decode_responses=True)

        self.table_name = table_name

    def __id(self, key):
        """
        类似于主键，用于创建一个不变的类别并存入相关的键
        :param key: 需要存入的key
        """
        old = self.redis_client.get(self.table_name)
        if old:
            old = json.loads(old)
        else:
            old = []
        if key not in old:
            old.append(key)
            new_data = json.dumps(old)
            self.redis_client.set(self.table_name, new_data)
            return True
        else:
            return False

    def __format_data(self, data):
        try:
            res = json.loads(data)
            return res
        except:
            ...
        return data

    def __fetch_all(self):
        key_list = self.redis_client.get(self.table_name)
        if key_list:
            key_list = json.loads(key_list)
            return key_list
        return None

    def __fetch_one(self, key):
        res = self.redis_client.get(key)
        if res:
            return self.__format_data(res)
        return None

    def fetch_one(self, key):
        """
        查询一个
        :param key: 指定key
        :return: dict | None
        """
        key = self.table_name + "_" + key
        data = self.__fetch_one(key)
        if data:
            return data
        return None

    def fetch_all(self, condition=None):
        """
        查询全部
        :param condition:
        :return: dict
        """
        res = []
        key_list = self.__fetch_all()
        if key_list:
            for key in key_list:
                data = self.__fetch_one(key)
                if data:
                    if condition:
                        c_keys = condition.keys()
                        for c_key in c_keys:
                            c_val = condition[c_key]
                            if data[c_key] == c_val:
                                data['_id'] = key
                                res.append(data)
                    else:
                        data['_id'] = key
                        res.append(data)
                else:
                    # 缓存值不见了 删掉这个key
                    ...
        return res

    def set(self, key, value):
        """
        存入一条数据
        :param key:
        :param value:
        :return: 已存在 ，成功
        """
        key = self.table_name + "_" + key
        __ids = self.__id(key)
        if not __ids:
            return 'model key exist'
        exist = self.redis_client.exists(key)
        if exist:
            return 'exist'
        else:
            self.redis_client.set(key, json.dumps(value))
            return 'add success'

    def update(self, key, value) -> bool:
        """
        更新一条数据
        当更新 字典时 直接传入 key:str->dict  value:dict
        当更新 列表时 直接传入 key:str->list  value:any 已修正重复录
        :param key:
        :param value:
        :return: Bool
        """
        key = self.table_name + "_" + key
        exist = self.redis_client.exists(key)
        if not exist:
            # raise "not exist"
            text = """cache error: The key is not exists,please check you key"""
            print(f"\033[35m {text} \033[0m")
            return False
        try:
            old = self.redis_client.get(key)
            old = json.loads(old)
            if type(old) is list:
                if value in old:
                    return True
                old.append(value)
            else:
                old.update(value)
            self.redis_client.set(key, json.dumps(old))
            return True
        except Exception as e:
            print(e)
            return False

    def delete(self, key):
        key = self.table_name + "_" + key
        self.redis_client.delete(key)
