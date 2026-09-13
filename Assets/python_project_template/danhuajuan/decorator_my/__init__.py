from pymongo.cursor import Cursor
from pymongo.synchronous.command_cursor import CommandCursor

from danhuajuan.config import ReadConfig
from danhuajuan.tools.t_snow_flake_id.t_snow_flake_id import uniqueness_id


def format_return(format_func):
    """通用装饰器，将函数返回值传递给格式化函数"""

    def decorator(func):
        def wrapper(*args, **kwargs):
            result = func(*args, **kwargs)
            return format_func(result)

        return wrapper

    return decorator


# 自定义格式化函数
def format_response_data(data, format_pass=None):
    if type(data) == Cursor or type(data) == CommandCursor or type(data) == list:
        new_data = []
        for x in data:
            x = __format_response_data_item(x, format_pass=format_pass)
            new_data.append(x)
        return new_data
    elif type(data) == dict:
        data = __format_response_data_item(data, format_pass=format_pass)
        return data
    elif type(data) == str:
        return data
    return data


def __format_response_data_item(data, format_pass=None):
    if '__v' in data:
        data.pop('__v')

    if '_id' in data:
        data['id'] = str(data['_id'])
        data.pop('_id')

    if 'password' in data:
        data['password'] = "权限不足"

    if 'security_data' in data:
        data['security_data'] = ""

    if 'game_pwd' in data:
        data.pop('game_pwd')

    if "machine_code" in data:
        data.pop("machine_code")

    if "finish" in data:
        if format_pass is None:
            if data['finish']:
                data['finish'] = "已完成"
            else:
                data['finish'] = "未完成"

    if "machine" in data:
        if data['machine'] == -1:
            data['machine'] = "-1"

    if "sell_price" in data:
        data['sell_price'] = float(data['sell_price'])

    if "safe_data" in data:
        data.pop("safe_data")

    return data
