"""
@File   :   read_config.py
@Date   :   2024/5/28 12:49
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import json
import os
import yaml


class ReadConfig:
    '''
    加载配置文件到内存中
    @param config_file_type 配置文件扩展名
            config_file 配置文件名称-不包含扩展名
    @return 配置文件内容 使用 res.data 获取
    '''

    def __init__(self, config_name: str, config_file_type: str = 'yml') -> None:

        self.config_file = os.path.join(os.path.dirname(__file__), '../../config/',
                                        config_name + '.' + config_file_type)
        self.data = None

        if config_file_type == 'env':
            ...
        elif config_file_type == 'json':
            self.__load_config_json()
        elif config_file_type == 'yml':
            self.__load_config_yaml()

    def __del__(self):
        return self.data

    def __load_config_json(self) -> dict:
        try:
            with open(self.config_file, 'r', encoding='utf-8') as file:
                config_data = json.load(file)
                self.data = config_data
            return config_data
        except Exception as e:
            print(f"json配置文件加载失败 ->{e}")
            self.data = {}
            return {}

    def __load_config_yaml(self) -> dict:
        with open(self.config_file, 'r') as file:
            data = yaml.safe_load(file)
            self.data = data
        return data
