import os
import smtplib
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText
from flask import render_template_string

from flask import current_app


def send_email(to_addr, subject, models, content):
    # 设置邮件信息
    '''
                    EMAIL_SEND_USER=seraphine-manager@.com.cn
                    EMAIL_PASSWORD=
                    EMAIL_HOST=
                    EMAIL_PORT=25
                    EMAIL_SUBJECT=soccerBaby
    :param to_addr:
    :param subject:
    :param models: balade下的 某个模板 例如 email/v_code.html
    :param content:
    :return:
    '''
    smtp_server = 'smtp-mail.outlook.com'
    smtp_port = 587
    username = 'xjpseraphine@outlook.com'
    password = os.environ.get('EMAIL_PASSWORD', '')  # 从环境变量读取，勿硬编码
    from_addr = 'xjpseraphine@outlook.com'

    # 从HTML文件中读取模板
    script_dir = os.path.dirname(os.path.abspath(__file__))  # 获得脚本所在目录的绝对路径
    template_path = os.path.join(script_dir, f"../blade/{models}")  # 将模板文件的路径和脚本目录拼接起来 '../blade/email/v_code.html'
    with open(template_path, 'r', encoding='utf-8') as file:
        html = file.read()

    # 使用 render_template_string 渲染模板并插入数据
    rendered_html = render_template_string(html, **content)
    with current_app.app_context():
        pass

    # 创建MIMEMultipart对象，这是整个邮件的容器
    msg = MIMEMultipart()
    msg['Subject'] = subject
    msg['From'] = from_addr
    msg['To'] = to_addr

    # 添加邮件正文：HTML版本
    msg.attach(MIMEText(rendered_html, 'html'))

    # 连接到SMTP服务器并发送邮件
    with smtplib.SMTP(smtp_server, smtp_port) as server:
        server.starttls()  # 启动TLS加密
        server.login(username, password)  # 登录到你的邮箱账户
        server.send_message(msg)  # 发送邮件

    return True
