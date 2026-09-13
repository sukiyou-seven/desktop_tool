import json
import time

import requests


class COAJ:
    def __init__(self):
        self.data = {
            "openapi": "3.0.1",
            "info": {
                "title": "",
                "description": "",
                "version": ""
            },
            "tags": [
                {
                    "name": "客户端"
                }
            ],
            "paths": {

            }
        }

    def create_example(self):
        ...

    def create_coaj(self, path, method, name, docstring):
        """
        :param path: 路由
        :param method: 方法
        :param name: apifox中显示的名称 定义路由视图函数的 endpoint 示例: app.route('/create', methods=['POST'],endpoint="注册")
        :param docstring: 测试用例
        :return:
        """
        paths_item = {
            f"{method.lower()}": {
                "summary": name.split(".")[-1],
                "deprecated": False,
                "description": "",
                "tags": [
                    name.split(".")[0]
                ],
                "parameters": [],
                "requestBody": self.__build_request_body(docstring),
                "security": [],
                "x-apifox-status": "developing",
                "responses": {}
            }
        }

        self.data['paths'][path] = paths_item

        # print(self.data)

    def __build_request_body(self, docstring):
        """
        根据视图函数 docstring 中的标记决定生成 application/json 还是 multipart/form-data。
        docstring 中含 "@content-type: multipart/form-data" 时生成 multipart,
        否则默认生成为 application/json。
        """
        docstring = docstring or ""
        lines = [line.strip() for line in docstring.splitlines()]
        is_multipart = any(
            line.lower().replace(" ", "").startswith("@content-type:multipart")
            for line in lines
        )

        if not is_multipart:
            return {
                "content": {
                    "application/json": {
                        "schema": {
                            "type": "object",
                            "properties": {}
                        },
                        "example": docstring
                    }
                },
                "required": True
            }

        # multipart/form-data: 解析 docstring 中形如 "字段名: 说明" 的字段列表,
        # 字段以 JSON 对象形式书写, 值中的文件类型说明会解析成 binary 上传字段。
        properties = self.__parse_form_fields(docstring)
        return {
            "content": {
                "multipart/form-data": {
                    "schema": {
                        "type": "object",
                        "properties": properties
                    },
                    "encoding": {}
                }
            },
            "required": True
        }

    def __parse_form_fields(self, docstring):
        """
        解析 multipart 字段。
        约定: docstring 第一行写 "@content-type: multipart/form-data",
        随后以 JSON 格式描述表单字段, 形如:
            {
                "file": "文件字段(必填, 单个, 类型 file)",
                "biz": "业务分类(可选)",
                "family_id": "家族ID(可选)"
            }
        值中若含文件/类型说明(如 "类型: file" / "file类型"), 该字段生成为 string + binary。
        """
        # 跳过空行、@标记行、注释说明行, 只保留 JSON 结构行(以 { } 或 " 开头)
        content = []
        for line in docstring.splitlines():
            stripped = line.strip()
            if not stripped:
                continue
            if stripped.startswith("@"):
                continue
            if stripped.startswith("#") or stripped.startswith("//"):
                continue
            content.append(line)
        properties = {}
        try:
            parsed = json.loads("\n".join(content).strip())
        except (json.JSONDecodeError, ValueError):
            return properties
        if not isinstance(parsed, dict):
            return properties

        def _schema_for(value):
            text = str(value)
            lower = text.lower()
            # 明确文件标记才判定为 binary(如 "类型 file" / "file类型" / binary)
            if "类型 file" in lower or "file类型" in lower or "binary" in lower:
                return {"type": "string", "format": "binary"}
            # 布尔/数值型猜测
            if "布尔" in text or "bool" in lower:
                return {"type": "boolean"}
            if "数字" in text or "int" in lower.replace("整数", "int"):
                return {"type": "integer"}
            return {"type": "string"}

        for key, value in parsed.items():
            properties[str(key)] = _schema_for(value)
        return properties

    def record_file(self):
        with open("openapi.json", "w", encoding="utf-8") as f:
            f.write(json.dumps(self.data, ensure_ascii=False))

    def add_to_apifox(self, project_id, api_token, folder_id, model_id):
        doit = True
        try:
            with open("import.id.lock", "r", encoding="utf-8") as f:
                res = f.read()
                if int(time.time()) - int(res) < 30:
                    print(f"导入间隔小于30秒，跳过导入,剩余{30 - (int(time.time()) - int(res))}秒")
                    doit = False
        except:
            print("读取导入间隔文件时出错")

        if doit:
            uri = f"https://api.apifox.com/v1/projects/{project_id}/import-openapi?locale=zh-CN"
            # print(self.data)
            payload = json.dumps({
                "input": json.dumps(self.data),
                "options": {
                    "targetEndpointFolderId": folder_id,
                    "targetSchemaFolderId": model_id,
                    "endpointOverwriteBehavior": "OVERWRITE_EXISTING",
                    "schemaOverwriteBehavior": "KEEP_EXISTING",
                    "updateFolderOfChangedEndpoint": False,
                    "prependBasePath": False
                }
            })
            headers = {
                'X-Apifox-Api-Version': '2024-03-28',
                'Authorization': f'Bearer {api_token}',
                'Content-Type': 'application/json'
            }

            response = requests.request("POST", uri, headers=headers, data=payload)

            print(response.text)

            with open("import.id.lock", "w", encoding="utf-8") as f:
                print("进行了导入，现在写入导入间隔文件")
                f.write(str(int(time.time())))
            f.close()
        # else:
        #     with open("/import.id", "w", encoding="utf-8") as f:
        #         print("进行了导入，现在写入导入间隔文件")
        #         f.write(str(int(time.time())))
        #     f.close()
