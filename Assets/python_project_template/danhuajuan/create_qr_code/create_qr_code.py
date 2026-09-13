import segno
import io
import base64


def generate_qr_base64(content: str, scale: int = 10) -> str:
    """
    使用 segno 生成二维码并转为 Base64
    :param content: 要编码的字符串
    :param scale:   放大倍数
    """
    # 1. 生成二维码（segno 默认输出 SVG/PIL 格式均可）
    qr = segno.make(content, error="m")  # 容错率: l/m/q/h

    # 2. 保存到内存
    buffer = io.BytesIO()
    qr.save(buffer, kind="png", scale=scale)

    # 3. 转 Base64
    b64_str = base64.b64encode(buffer.getvalue()).decode("utf-8")
    return f"data:image/png;base64,{b64_str}"


# 使用示例
# if __name__ == "__main__":
#     print(generate_qr_base64("Hello, World!")[:80] + "...")