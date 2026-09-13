"""
@File   :   t_upload.py
@Date   :   2024/6/13 16:23
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import os

from flask import current_app

from danhuajuan.tools.t_snow_flake_id.t_snow_flake_id import uniqueness_id


class Upload:
    """
    通用文件上传工具
    文件统一保存到 static/ 目录下, 通过 image_prefix 拼出可访问的完整 URL
    目录结构: static/{biz}[/{family_id}]/{雪花ID}.{ext}
    """

    # 允许上传的扩展名白名单
    ALLOW_EXTENSIONS = {
        # 图片
        "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "ico", "heic",
        # 文档
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "md",
        "csv", "ged", "gedcom",
        # 音视频
        "mp3", "wav", "mp4", "avi", "mov", "webm", "mkv",
        # 压缩包
        "zip", "rar", "7z",
    }

    # 图片扩展名
    IMAGE_EXTENSIONS = {"jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "ico", "heic"}
    # 音视频扩展名
    MEDIA_EXTENSIONS = {"mp3", "wav", "mp4", "avi", "mov", "webm", "mkv"}

    DEFAULT_MAX_SIZE = 20 * 1024 * 1024  # 默认单文件 20MB

    def __init__(self, max_size=DEFAULT_MAX_SIZE, static_root="static"):
        """
        :param max_size: 单文件大小上限(字节)
        :param static_root: 静态文件根目录(相对项目根目录)
        """
        self.__max_size = max_size
        self.__static_root = static_root

    def __check_ext(self, filename):
        """校验扩展名, 合法返回小写扩展名, 不合法返回 None"""
        if not filename or "." not in filename:
            return None
        ext = filename.rsplit(".", 1)[-1].strip().lower()
        return ext if ext in self.ALLOW_EXTENSIONS else None

    def __file_category(self, ext):
        """根据扩展名返回文件分类: image / media / file"""
        if ext in self.IMAGE_EXTENSIONS:
            return "image"
        if ext in self.MEDIA_EXTENSIONS:
            return "media"
        return "file"

    def upload(self, file, biz="common", family_id=None):
        """
        上传单个文件
        :param file: werkzeug.datastructures.FileStorage
        :param biz: 业务分类, 作为一级目录
        :param family_id: 家族ID(可选), 存在时归档到 biz/{family_id} 目录
        :return: {
            "file_name": 原始文件名,
            "new_name": 服务器保存的文件名,
            "save_path": 相对项目根目录的保存路径,
            "url": 完整访问URL,
            "size": 文件大小(字节),
            "file_type": image/media/file
        }
        """
        if not file or not file.filename:
            raise ValueError("未获取到文件")

        ext = self.__check_ext(file.filename)
        if ext is None:
            raise ValueError(f"不支持的文件类型: {file.filename}")

        file.seek(0, os.SEEK_END)
        size = file.tell()
        file.seek(0)
        if size > self.__max_size:
            raise ValueError(f"文件大小超过限制({self.__max_size // 1024 // 1024}MB)")

        # 保存目录: static/{biz}[/{family_id}]
        save_dir = os.path.join(self.__static_root, biz)
        if family_id:
            save_dir = os.path.join(save_dir, str(family_id))
        os.makedirs(save_dir, exist_ok=True)

        # 雪花ID生成唯一文件名, 保留原扩展名, 避免重名与路径穿越
        new_name = f"{uniqueness_id('F_').res}.{ext}"
        file.save(os.path.join(save_dir, new_name))

        # 访问相对路径 与 完整URL
        rel_path = f"{biz}/{family_id}/{new_name}" if family_id else f"{biz}/{new_name}"
        image_prefix = current_app.config.get("image_prefix", "/static/")
        url = f"{image_prefix}{rel_path}"

        return {
            "file_name": file.filename,
            "new_name": new_name,
            "save_path": f"{self.__static_root}/{rel_path}",
            "url": url,
            "size": size,
            "file_type": self.__file_category(ext),
        }

    def upload_multi(self, files, biz="common", family_id=None):
        """
        批量上传
        :param files: FileStorage 列表
        :return: {"success": [单个上传结果...], "failed": [{"file_name", "reason"}...]}
        """
        result = {"success": [], "failed": []}
        for file in files:
            try:
                result["success"].append(self.upload(file, biz=biz, family_id=family_id))
            except Exception as e:
                result["failed"].append({
                    "file_name": getattr(file, "filename", "") or "",
                    "reason": str(e),
                })
        return result
