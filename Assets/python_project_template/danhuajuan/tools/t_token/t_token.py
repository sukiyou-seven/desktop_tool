"""
@File   :   t_token.py
@Date   :   2024/5/28 13:50
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import datetime

import jwt

from danhuajuan.config import ReadConfig


class TToken:
    def __init__(self):
        ...

    def create(self, data, expire_time=86400 * 7):
        config_secret = ReadConfig('secret', 'yml').data
        secret = config_secret['secret']

        payload = {
            "data": data,
            "exp": datetime.datetime.utcnow() + datetime.timedelta(seconds=expire_time),
            # 发行人
            "iss": config_secret['iss']
        }
        encoded_jwt = jwt.encode(payload, secret, algorithm='HS256')
        refresh_token = self.refresh_token(data)
        return encoded_jwt,refresh_token


    def refresh_token(self,data):
        config_secret = ReadConfig('secret', 'yml').data
        secret = config_secret['secret']

        payload = {
            "data": data,
            "iss": config_secret['iss']
        }
        encoded_jwt = jwt.encode(payload, secret, algorithm='HS256')
        return encoded_jwt

    def decode(self, token):
        config_secret = ReadConfig('secret', 'yml').data
        secret = config_secret['secret']
        try:
            decoded_jwt = jwt.decode(token, secret, algorithms=['HS256'])
            return decoded_jwt
        except jwt.ExpiredSignatureError:
            return "EXPIRED"
            # raise Exception("EXPIRED")
        except jwt.InvalidTokenError:
            return "INVALID"
            # raise Exception("用户认证失败,请重新登录")
        except:
            return "ERROR"
            raise ValueError("token错误")


if __name__ == '__main__':
    t = TToken()
    token,refresh_token = t.create({"user_id": "123456"})
    print(token)
    print(refresh_token)
    decoded_jwt = t.decode("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJkYXRhIjp7InVzZXJfaWQiOiJ1c2VyXzYwOTgzMTc5MTM2NDI4MDMyMCIsIm5pY2tuYW1lIjoibmljayJ9LCJleHAiOjE3ODcxMjQyNzcsImlzcyI6Imh1YW1haV9haV96dXB1X2FwcGxpY2F0aW9uIn0.CYzFZuFWCrEo9yrr0l1xTd60GWzxsR01FYOi9nzKmT0")
    print(decoded_jwt['data']['user_id'])