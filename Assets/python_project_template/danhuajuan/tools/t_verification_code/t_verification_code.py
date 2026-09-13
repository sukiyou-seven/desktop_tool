"""
@File   :   t_verification_code.py
@Date   :   2024/5/28 13:58
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import random


class TVerificationCode:
    def __init__(self,leng = 6):
        self.code = ''
        for _ in range(6):
            self.code += str(random.randint(0, 9))

    def __del__(self):
        return self.code