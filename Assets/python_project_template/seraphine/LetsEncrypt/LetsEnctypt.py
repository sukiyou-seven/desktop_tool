import os
import sys
import json
import time
import signal
import subprocess
import yaml

# ==========================================
# 顶部配置变量 (请在此处修改您的配置)
# ==========================================
DOMAIN = "example.com.cn"  # 替换为您要申请证书的域名
EMAIL = "abc@163.com"  # 替换为您的邮箱（用于接收证书过期提醒）
CERTBOT_EXEC = "certbot"  # certbot 可执行命令，若不在系统环境变量中请填写绝对路径


# ==========================================
def read_config():
    global DOMAIN, EMAIL, CERTBOT_EXEC
    try:
        with open("config.yml", 'r', encoding='utf-8') as f:
            config_data = yaml.safe_load(f)
            DOMAIN = config_data['domain']
            EMAIL = config_data['email']
            CERTBOT_EXEC = config_data['certbot_exec']
            print("[*] 配置信息读取完成")
    except Exception as e:
        print(e)
        print("[❌] 配置文件不存在")
        print("[*] 创建配置文件")
        with open("config.yml", 'w', encoding='utf-8') as f:
            f.write("domain: 域名 \remail: 账号-填入有效邮箱即可 \rcertbot_exec: certbot # 这里写死无需更改 \r")
        f.close()
        print("[✔] 配置文件已生成,10s后自动关闭程序,请修改配置文件后,再次启动")
        time.sleep(10)
        exit()


print("[*] 正在读取配置信息...")
read_config()

print(f"[+] 当前申请域名:{DOMAIN} ")
print(f"[+] 当前使用账号:{EMAIL} ")
print(f"[+] 申请模式:DNS ")
print(f"[+] 申请使用:{CERTBOT_EXEC} ")
print("\r")
print(f"[*] 如遇 certbot 错误 请尝试安装certbot pip install certbot / uv add certbot")

# 内部路径与状态文件初始化 (基于脚本所在目录)
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
CERT_DIR = os.path.join(BASE_DIR, "certs")
WORK_DIR = os.path.join(BASE_DIR, "certbot_work")
CHALLENGE_FILE = os.path.join(BASE_DIR, "dns_challenge.json")
SIGNAL_FILE = os.path.join(BASE_DIR, "dns_continue.signal")
PID_FILE = os.path.join(BASE_DIR, "certbot.pid")
AUTH_HOOK_SCRIPT = os.path.join(BASE_DIR, "_auth_hook.py")
CLEANUP_HOOK_SCRIPT = os.path.join(BASE_DIR, "_cleanup_hook.py")


def generate_hooks():
    """动态生成 Certbot 所需的 Auth 和 Cleanup Hook 脚本"""
    # 1. Auth Hook: 记录 Challenge 并阻塞等待信号
    auth_code = f"""
import os, json, time 
domain = os.environ.get('CERTBOT_DOMAIN')
token = os.environ.get('CERTBOT_VALIDATION')
data = {{'domain': domain, 'token': token, 'record_name': '_acme-challenge.' + domain}} 
with open(r'{CHALLENGE_FILE}', 'w') as f:
    json.dump(data, f)
# 阻塞等待第二步的信号文件 
while not os.path.exists(r'{SIGNAL_FILE}'):
    time.sleep(2)
"""
    with open(AUTH_HOOK_SCRIPT, 'w', encoding='utf-8') as f:
        f.write(auth_code)

        # 2. Cleanup Hook: 验证结束后清理临时状态文件
    cleanup_code = f"""
import os 
for f in [r'{SIGNAL_FILE}', r'{CHALLENGE_FILE}']:
    if os.path.exists(f):
        os.remove(f)
"""
    with open(CLEANUP_HOOK_SCRIPT, 'w', encoding='utf-8') as f:
        f.write(cleanup_code)


def terminate_old_process():
    """清理可能残留的旧 Certbot 进程"""
    if os.path.exists(PID_FILE):
        with open(PID_FILE, 'r') as f:
            old_pid = int(f.read().strip())
        try:
            os.kill(old_pid, 0)  # 探活
            print(f"[*] 发现残留的 Certbot 进程 (PID: {old_pid})，正在终止...")
            os.kill(old_pid, signal.SIGTERM)
            time.sleep(2)
        except OSError:
            pass
        os.remove(PID_FILE)


def step1():
    print("\n" + "=" * 50)
    print(" 第一步：获取 DNS 解析记录")
    print("=" * 50)

    # 清理旧状态 
    terminate_old_process()
    for f in [CHALLENGE_FILE, SIGNAL_FILE]:
        if os.path.exists(f):
            os.remove(f)

    generate_hooks()
    os.makedirs(CERT_DIR, exist_ok=True)
    os.makedirs(WORK_DIR, exist_ok=True)

    # 构建 Certbot 命令
    cmd = [
        CERTBOT_EXEC, "certonly",
        "--manual", "--preferred-challenges", "dns",
        "--manual-auth-hook", f"{sys.executable} {AUTH_HOOK_SCRIPT}",
        "--manual-cleanup-hook", f"{sys.executable} {CLEANUP_HOOK_SCRIPT}",
        "-d", DOMAIN, "--email", EMAIL,
        "--agree-tos", "--non-interactive", "--no-eff-email",
        "--config-dir", CERT_DIR, "--work-dir", WORK_DIR,
        "--logs-dir", os.path.join(WORK_DIR, "logs")
    ]

    print("[*] 正在后台启动 Certbot...")
    log_file = open(os.path.join(WORK_DIR, "certbot_run.log"), "w", encoding='utf-8')
    process = subprocess.Popen(cmd, stdout=log_file, stderr=subprocess.STDOUT)

    with open(PID_FILE, 'w') as f:
        f.write(str(process.pid))

    print("[*] 等待 Certbot 生成 DNS Challenge...")
    timeout = 60
    start_time = time.time()

    # 等待 Hook 脚本写入 Challenge 文件
    while not os.path.exists(CHALLENGE_FILE):
        if time.time() - start_time > timeout:
            print("[!] 超时：Certbot 未能及时生成 Challenge，请检查日志：", log_file.name)
            process.terminate()
            return
        if process.poll() is not None:
            print("[!] Certbot 进程意外退出，请检查日志：", log_file.name)
            return
        time.sleep(1)

    with open(CHALLENGE_FILE, 'r', encoding='utf-8') as f:
        data = json.load(f)

    print("\n" + "*" * 50)
    print(" 请在您的 DNS 服务商 (或手机 App) 处添加以下 TXT 记录：")
    print(f" 主机记录 (Name/Host) : {data['record_name']}")
    print(f" 记录值 (Value/Content): {data['token']}")
    print(" 记录类型 (Type)      : TXT")
    print("*" * 50 + "\n")
    print("[+] 第一步完成！Certbot 已在后台挂起等待。")
    print("[+] 添加完成后，请重新运行本脚本并选择【第二步】。")


def step2():
    print("\n" + "=" * 50)
    print(" 第二步：验证并请求证书")
    print("=" * 50)

    if not os.path.exists(CHALLENGE_FILE):
        print("[!] 错误：未找到 Challenge 信息，请先执行第一步。")
        return

    with open(CHALLENGE_FILE, 'r', encoding='utf-8') as f:
        data = json.load(f)
    print(f"[*] 将验证域名: {data['domain']}")

    input("\n[?] 请确保 DNS 记录已添加并生效 (按回车继续，或 Ctrl+C 取消)...")

    # 创建信号文件，唤醒后台的 Auth Hook
    with open(SIGNAL_FILE, 'w') as f:
        f.write("continue")
    print("[*] 已发送继续信号，正在等待 Certbot 完成验证与证书下发...")

    # 读取 PID 并等待进程结束 
    if os.path.exists(PID_FILE):
        with open(PID_FILE, 'r') as f:
            pid = int(f.read().strip())

        while True:
            try:
                os.kill(pid, 0)  # 探活：如果进程存在则不抛出异常
                time.sleep(2)
            except OSError:
                break

    print("\n[+] Certbot 运行结束！")
    print(f"[+] 运行日志: {os.path.join(WORK_DIR, 'certbot_run.log')}")
    print(f"[+] 证书目录: {os.path.join(CERT_DIR, 'live', DOMAIN)}")
    print("[+] 请查看日志确认是否显示 'Congratulations!'。")

    print("\n[*] Certbot 运行结束！正在分析结果...")
    log_path = os.path.join(WORK_DIR, "certbot_run.log")

    is_success = False
    if os.path.exists(log_path):
        with open(log_path, 'r', encoding='utf-8') as f:
            log_content = f.read()
            if "Successfully" in log_content:
                is_success = True

    if is_success:
        print("\n" + "=" * 50)
        print(" 🎉 证书申请成功！")
        print(f" 📂 证书目录: {os.path.join(CERT_DIR, 'live', DOMAIN)}")
        print("=" * 50)
    else:
        print("\n" + "!" * 50)
        print(" ❌ 证书申请失败！")
        print(f" 📄 请务必查看日志排查原因: {log_path}")
        print("!" * 50)


def main():
    print("\n" + "=" * 20 + " Let's Encrypt 证书申请工具 " + "=" * 20)
    print(" 1. 第一步：获取 DNS 解析记录 (后台挂起 Certbot)")
    print(" 2. 第二步：验证并获取证书 (唤醒 Certbot)")
    print(" 3. 退出")
    print("=" * 68)
    choice = input("请输入选项 (1/2/3): ").strip()

    if choice == '1':
        step1()
    elif choice == '2':
        step2()
    elif choice == '3':
        sys.exit(0)
    else:
        print("[!] 无效选项，请重新运行。")


if __name__ == "__main__":
    main()
