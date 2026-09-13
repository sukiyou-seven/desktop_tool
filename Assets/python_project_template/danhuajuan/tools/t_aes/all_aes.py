"""
@file   :   all_aes.py
@date   :   2024/12/30 15:20
@author :   sukiyou
@version:   0.0.1
@contact:   danhuajuan_sukiyou@163.com
@license:   MIT
@desc   :   wlp  应用于 密文+key+iv 拼接模式的 加密  对接C#
"""

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.backends import default_backend
from cryptography.hazmat.primitives.padding import PKCS7
import os
import base64


class TAllAes:
    def __init__(self):
        self.__key = b""
        self.__iv = b""

    def __create_key__(self):
        self.__key = os.urandom(32)

    def __create_iv__(self):
        self.__iv = os.urandom(16)

    def encrypt(self, plaintext):
        self.__create_key__()
        self.__create_iv__()

        plaintext_bytes = plaintext.encode()
        padder = PKCS7(128).padder()
        padded_data = padder.update(plaintext_bytes) + padder.finalize()
        cipher = Cipher(algorithms.AES(self.__key), modes.CBC(self.__iv), backend=default_backend())
        encryptor = cipher.encryptor()
        ciphertext = encryptor.update(padded_data) + encryptor.finalize()
        encoded_ciphertext = base64.b64encode(ciphertext).decode()

        return {
            "key": self.__key,
            "iv": self.__iv,
            "keyb": base64.b64encode(self.__key),
            "ivb": base64.b64encode(self.__iv),
            "encrypted_data": encoded_ciphertext,
            "res": f"{encoded_ciphertext}{base64.b64encode(self.__key).decode()}{base64.b64encode(self.__iv).decode()}"
        }

    def decrypt(self, encrypted_data):
        # 提取 IV 和 Key
        ivs = encrypted_data[-24:]
        iv = base64.b64decode(ivs)
        keys = encrypted_data[-68:-24]
        key = base64.b64decode(keys)

        # 提取加密数据
        encrypted_data = encrypted_data[:-68]


        # 解码加密数据
        encrypted_data_bytes = base64.b64decode(encrypted_data)

        # 创建 AES 解密器
        backend = default_backend()
        cipher = Cipher(algorithms.AES(key), modes.CBC(iv), backend=backend)
        decryptor = cipher.decryptor()

        # 解密数据
        padded_plaintext = decryptor.update(encrypted_data_bytes) + decryptor.finalize()


        # 去填充
        unpadder = padding.PKCS7(128).unpadder()
        plaintext = unpadder.update(padded_plaintext) + unpadder.finalize()
        return plaintext.decode('utf-8')


# a = TAllAes()
# ret = a.encrypt("123")
# print("ret:",ret)
# res = a.decrypt("Hzlis/O/vtR8ipPkd/SpXQ==eh6ERwU5y8+OchgoKaVv6bjnDMbWl+/KWRiPbMrddYw=KvSSjvJoPMcbH3kvQcYPRQ==")
#
# print("res:",res)
# R+7CmUZMxXalD9WiTjORjg==YYDs7tWsrWENEeYdeUqgxP0Pfj0uaZwKZvC39nmfvfg=KlGRaJAGmvWWgn45ltp10w==
# e4daad24d8b6e89819f70fb704eee3bfyiEg6A6YvdjB5/Do7aQx4J7+usTUgS1zy4fdKWKifWY=34JvogqMRhfDo3S20AlOXw==