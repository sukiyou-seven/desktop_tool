"""
@File   :   t_totp.py
@Date   :   2024/5/28 13:57
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import base64
import os
import pyotp
from datetime import timedelta
from datetime import datetime


class TTOTP:

    def __init__(self, name: str = 'default', tag: str = 'default'):
        """
        初始化类后 使用 class().res来获取 totp 密钥
        使用 class.validate(用户输入的动态码,该用户的密钥) 来获取 验证是否通过
        @param name:
        @param tag:
        """
        secret = os.urandom(20)
        # 将秘密转换为Base32编码
        self.res = base64.b32encode(secret).decode('utf-8')
        self.secret_base32_url = f"otpauth://totp/{name}:{tag}?secret={self.res}"
        self.totp = pyotp.TOTP(self.res)
        self.now = datetime.utcnow().timestamp()

    def validate(self, otp_input: str, secret_input: str) -> bool:
        # 创建一个TOTP对象
        authenticator = pyotp.TOTP(secret_input)
        # 获取当前时间窗口下的正确TOTP值
        current_otp = authenticator.now()
        print(f"当前正确的TOTP密码是: {current_otp}")

        # 计算剩余有效时间
        now = datetime.utcnow()
        time_step = 30  # TOTP 默认时间步长为30秒
        next_window = (int(now.timestamp() // time_step) + 1) * time_step
        remaining_time = next_window - int(now.timestamp())
        print(f"当前密码剩余有效时间: {remaining_time} 秒")

        # 验证密码
        if authenticator.verify(otp_input,valid_window=1):
            return True
        else:
            return False


# if __name__ == '__main__':
#     totp = TTOTP(name="Nexore",tag="NexoreAuth")
#     print(totp.res)
#     print(totp.secret_base32_url)
#     r = totp.validate('511006', 'PZ3Z2MLFBEBY4DQZF5FAW7VCUJO555BN')
#     print(r)
