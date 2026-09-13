"""
@File   :   t_aes.py
@Date   :   2024/6/11 02:01
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.backends import default_backend
from cryptography.hazmat.primitives.padding import PKCS7
import os
import base64

class TAes:
    def __init__(self):
        self.key = b"d2m6y9q4w8n2b2g7h8p5s8j6w1t2e4d6"
        ...

    def __pkcs7_padding(self, data):
        # 计算需要填充的字节数
        block_size = 16
        padding_length = block_size - (len(data) % block_size)
        # 生成填充字节
        padding = bytes([padding_length]) * padding_length
        # 将填充字节添加到数据末尾
        return data + padding

    def encrypt(self, plaintext: str, key: str = None, ivs: str = None) -> str:
        # 将明文转换为字节
        plaintext_bytes = plaintext.encode()

        padder = PKCS7(algorithms.AES.block_size).padder()

        # 对明文进行 PKCS#7 填充
        plaintext_bytes =  padder.update(plaintext_bytes)  + padder.finalize()

        # 生成随机初始化向量（IV）
        iv = os.urandom(16)

        if key:
            self.key = key.encode()
        if iv:
            iv = ivs.encode()
        # 创建具有AES算法和CBC模式的Cipher对象
        cipher = Cipher(algorithms.AES(self.key), modes.CBC(iv), backend=default_backend())

        # 创建加密器对象
        encryptor = cipher.encryptor()

        # 加密明文
        ciphertext = encryptor.update(plaintext_bytes) + encryptor.finalize()

        # 将IV和密文组合
        combined = ciphertext
        # Base64对组合数据进行编码
        encoded = base64.b64encode(combined).decode()
        return encoded

    # 解密函数
    def decrypt(self, encrypted_data, iv_hex):
        # 将16进制的IV转换为字节串
        iv = bytes.fromhex(iv_hex)

        # 使用UTF-8编码处理密钥
        key = self.key

        # 创建一个AES cipher对象，使用CBC模式和给定的密钥与IV
        backend = default_backend()
        cipher = Cipher(algorithms.AES(key), modes.CBC(iv), backend=backend)

        # 解码加密数据并准备解密
        encrypted_data_bytes = base64.b64decode(encrypted_data)

        # 创建一个解密器
        decryptor = cipher.decryptor()

        # 解密数据
        padded_plaintext = decryptor.update(encrypted_data_bytes) + decryptor.finalize()

        # 移除PKCS7填充
        unpadder = PKCS7(128).unpadder()
        plaintext = unpadder.update(padded_plaintext) + unpadder.finalize()

        return plaintext.decode('utf-8')


# if __name__ == '__main__':
#     aes = TAes()
#     a = aes.decrypt("1564eg81vd5hbm90123rj7n0vcsdurghn46902=fhj3w48u9bhsjfg;n645ui9-yhj", b"d2m6y9q4w8n2b2g7h8p5s8j6w1t2e4d6")
#     print(f"a=>{a}")
