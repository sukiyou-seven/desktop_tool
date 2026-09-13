import base64

import requests


def url_to_base64(image_url):
    try:
        # 1. 发送 GET 请求获取图片内容
        response = requests.get(image_url, timeout=10)
        response.raise_for_status()  # 检查请求是否成功

        # 2. 将二进制内容编码为 Base64 字节
        base64_bytes = base64.b64encode(response.content)

        # 3. 转换为字符串并返回
        return "data:image/jpeg;base64,"+base64_bytes.decode('utf-8')
    except Exception as e:
        print(f"转换失败：{e}")
        return None