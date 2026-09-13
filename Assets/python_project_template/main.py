import json
import os
import threading
import time

from flask import Flask, request, g, jsonify, render_template
from flask_cors import CORS
from flask_pymongo import PyMongo

# from database.databases import db

from danhuajuan.config import ReadConfig
from danhuajuan.tools.t_g_config.t_g_config import TGConfig
from danhuajuan.tools.t_upload.t_upload import Upload



def create_app():
    __config = ReadConfig("app").data

    config_app = __config['app']

    __app = Flask(__name__, static_folder="static/")

    CORS(__app,
         resources=config_app['resources'],
         origins=config_app['origins'],
         allow_headers=config_app['allow_headers'],
         expose_headers=config_app['expose_headers'],
         supports_credentials=config_app['supports_credentials'],
         methods=config_app['methods']
         )

    __app.config['image_prefix'] = config_app['image_prefix']

    config_mongo = __config['mongo']
    __app.config["MONGO_URI"] = ( #MONGO_URI
        f"mongodb://{config_mongo['username']}:{config_mongo['password']}@{config_mongo['host']}:"
        f"{config_mongo['port']}/{config_mongo['database']}?authSource={config_mongo['auth_source']}&maxPoolSize=100")
    mongo = PyMongo(__app)
    __app.config['mongo'] = mongo
    __app.config['SQLALCHEMY_DATABASE_URI'] = mongo


    @__app.before_request
    def before_request():
        if request.method == 'OPTIONS':
            return "200", 200

    @__app.after_request
    def after_request(response):
        # 文件下载接口直接跳过全部包装逻辑
        if response.headers.get("Content-Disposition", "").startswith("attachment"):
            return response

        if request.path.startswith('/static/'):
            return response
        # 条款类 支持一下get访问
        if request.method == "GET":
            if request.path.startswith('/clause/'):
                return response
        try:
            data = json.loads(response.data)
        except json.decoder.JSONDecodeError as e:
            try:
                data = response.data.decode("utf8")
            except UnicodeDecodeError as e:
                data = response.data

        new_response = {
            'code': getattr(g, 'code', "0"),
            'message': getattr(g, 'message', 'success'),
            'data': data,
            "some_sign": "app",
            "name":getattr(g, 'name', 'app'),
            'config': {
                'X-Token': getattr(g, 'token', False),
                'X-Refresh': getattr(g, 'refresh_token', False)
            },
            "description": getattr(g, 'description', 'no')
        }

        finally_response = jsonify(new_response)
        finally_response.headers.add('opeind', '1234')


        return finally_response


    @__app.errorhandler(404)
    def not_found(e):
        if request.method == "GET":
            return render_template("404.html")
        return "当前路由不存在"

    @__app.errorhandler(500)
    def internal_server_error(e):
        print("500 run")
        try:
            if request.method == "GET":
                return render_template("500.html")
        except:
            return "服务器内部错误"
        return "服务器内部错误"
    # ------------ 路由 begin---------------

    # 统一文件上传接口
    @__app.route('/api/upload', methods=['POST'], endpoint="统一文件上传")
    def upload_file():
        """
        @content-type: multipart/form-data
        {
            "file": "文件字段(必填, 单个, 类型 file)",
            "biz": "业务分类(可选)",
            "family_id": "家族ID(可选)"
        }
        """
        up = Upload()
        file = request.files.get("file")
        if file is None:
            TGConfig("未上传文件", "UPLOAD_EMPTY")
            return "error"
        biz = request.form.get("biz", "common")
        family_id = request.form.get("family_id")
        try:
            res = up.upload(file, biz=biz, family_id=family_id)
        except ValueError as e:
            TGConfig(str(e), "UPLOAD_ERROR")
            return "error"
        return res

    # 统一批量文件上传接口
    @__app.route('/api/upload/multi', methods=['POST'], endpoint="统一批量文件上传")
    def upload_file_multi():
        """
        @content-type: multipart/form-data
        {
            "files": "文件字段(必填, 可多个, 类型 file)",
            "biz": "业务分类(可选)",
            "family_id": "家族ID(可选)"
        }
        """
        up = Upload()
        files = request.files.getlist("files")
        if not files or all(not f.filename for f in files):
            TGConfig("未上传文件", "UPLOAD_EMPTY")
            return "error"
        biz = request.form.get("biz", "common")
        family_id = request.form.get("family_id")
        return up.upload_multi(files, biz=biz, family_id=family_id)

    # app.register_blueprint(plugin,url_prefix='/plugin')
    # app.register_blueprint(client_user,url_prefix="/client_user")
    # app.register_blueprint(tx_server,url_prefix="/tx_server")

    # from route.client.user import client_user
    # __app.register_blueprint(client_user, url_prefix="/client_user")
    # from route.family.family import family
    # __app.register_blueprint(family, url_prefix="/family")

    # from route.clause.clause import clause
    # __app.register_blueprint(clause,url_prefix="/clause")

    # from route.helps.helps import helps
    # __app.register_blueprint(helps, url_prefix="/helps")

    # ------------ socket ---------------
    socketio = None
    # app.config['SECRET_KEY'] = 'secret!'
    # socketio = SocketIO(app, cors_allowed_origins="*")
    #
    # app.config['socket'] = socketio

    # ------------ socket ---------------

    return __app,socketio


app,si = create_app()

socket_map = {}

# @si.on("connect")
# def connects():
#     print(f"连接的客户端:{request.sid}")
#     emit("connect success")

# 兼容使用 application作为app对象的 uwsgi

def print_routes(app):
    W_ENDPOINT = 50  # 「来源」列固定宽22
    W_RULE = 50  # 「路由」列固定宽40
    W_METHOD = 15  # 「方法」列固定宽15
    try:
        os.remove("route.txt")
    except:
        ...
    ___config = ReadConfig("app").data

    from seraphine.CreateOpenApiJson.coaj import COAJ
    c = COAJ()
    import inspect
    for rule in app.url_map.iter_rules():
        methods = ",".join(sorted(rule.methods - {"OPTIONS", "HEAD"}))

        docstring = {} # 测试用例 写在路由视图函数的函数注释中 仅写这一个
        view_func = app.view_functions.get(rule.endpoint) # 拿路由视图函数

        # @Description : 尝试进行 ApiFox 接口信息导入
        if view_func:
            docstring = inspect.getdoc(view_func) # 读取注释
        try:
            c.create_coaj(rule.rule, methods, rule.endpoint, docstring)
        except:
            print("尝试导入apifox失败")



        # print(f"来源: {rule.endpoint},  路由: {rule.rule},  方法: {rule.methods}")

        if ___config['app']['log_route']:
            print(
                f"来源: {rule.endpoint:<{W_ENDPOINT}.{W_ENDPOINT}}"
                f"路由: {rule.rule:<{W_RULE}.{W_RULE}}"
                f"方法: [{methods}]"
            )
        if ___config['app']['file_route']:
            with open("route.txt", "a", encoding="utf-8") as f:
                f.write(
                    f"来源: {rule.endpoint:<{W_ENDPOINT}.{W_ENDPOINT}}"
                    f"路由: {rule.rule:<{W_RULE}.{W_RULE}}"
                    f"方法: [{methods}]\n"
                )
    __apifox_config = ReadConfig("apifox").data

    c.record_file()
    c.add_to_apifox(
        __apifox_config['project_id'],
        __apifox_config['api_token'],
        __apifox_config['folder_id'],
        __apifox_config['model_id']
    )
if __name__ == '__main__':
    config = ReadConfig("app").data
    host = config['app']['host']
    port = config['app']['port']
    port_check = config['app']['port_check']
    if not (50000 <= port <= 59999) and port_check:
        raise Exception("请注意，您现在使用的端口号可能与其他业务冲突 "
                        "默认支持 50000 - 59999 "
                        "如果您可以确定端口号不会冲突，请在配置文件中将 app.port_check 改为 False 即可")
    debug = config['app']['debug']

    print_routes(app)



    app.run(host=host, port=port, debug=debug, threaded=True)


    # si.run(app=app, host=host, port=port, debug=debug, allow_unsafe_werkzeug=True)


#pyinstaller -F app.py --add-data ".\get_wy_qnyh_cookie.py;." -i .\wpfui-icon.ico --clean -w