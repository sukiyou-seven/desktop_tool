import hashlib

from urllib.parse import urlencode, quote

def signs(request_data,key):
    """
    :param request_data: 请求参数
    :param key: 密钥
    """
    # 1. 过滤 'sign' 键并按字母顺序排序
    # 字典推导式排除敏感字段，sorted 确保键顺序固定
    filtered_data = {k: v for k, v in request_data.items() if k != 'sign'}
    sorted_items = sorted(filtered_data.items())

    # 2. 生成参数字符串 (自动处理 URL 编码)
    # quote_via=quote 确保空格编码为 %20 而非 +，更贴近 encodeURIComponent
    paramString = urlencode(sorted_items)

    # 3. 拼接密钥生成待签名字符串
    stringSignTemp = f"{paramString}&key={key}"

    sign = hashlib.md5(stringSignTemp.encode()).hexdigest().upper()
    return sign