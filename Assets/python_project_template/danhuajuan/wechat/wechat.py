"""
@File   :   wechat.py
@Date   :   2024/5/28 14:08
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
from conf.read_config import ReadConfig


class Wechat:
    def __init__(self):
        config_wechat = ReadConfig('wechat').data
        self.appid = config_wechat['wechat']['appid']
        self.secret = config_wechat['wechat']['secret']