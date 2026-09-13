"""
@File   :   login.py
@Date   :   2024/5/28 14:08
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import requests

from danhuajuan.wechat.wechat import Wechat


class Login(Wechat):

    def login(self, code: str):
        uri = "https://api.weixin.qq.com/sns/jscode2session"
        grant_type = "authorization_code"
        res = requests.get(
            url=f"{uri}?appid={self.appid}&secret={self.secret}&js_code={code}&grant_type={grant_type}"
        )

        res = res.json()
        if res.get('errcode') is not None and res.get('errcode') != 0:
            raise Exception(res.get('errmsg'))

        if res.get('openid'):
            return res
        else:
            return None
