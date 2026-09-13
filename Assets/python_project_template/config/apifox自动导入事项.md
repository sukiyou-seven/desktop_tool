在route中 指定 endpoint apifox中将显示此名称  
视图函数注释会作为 测试实例数据  
自动标识 请求方法  

seraphine/CreateOpenApiJson/coaj.py 将自动生成 openapi/swagger 导入json


```python

@client_user.route('/create', methods=['POST'], endpoint="注册")
def create_user():
    """
    {
        "user_email": "user@example1.com",
        "password": "password123",
        "devices_info": {
            "device_id": "abc123"
        },
        "nickname": "nick",
        "email_code": "050722"
    }
    """
    data = request.get_json()
    user_email = data.get('user_email')
    password = data.get('password')
    devices_info = data.get('devices_info')["device_id"]
    nickname = data.get('nickname')
    email_code = data.get('email_code')

    res = ClientUser().create_user(user_email, password, devices_info, nickname, email_code)

    return res


```