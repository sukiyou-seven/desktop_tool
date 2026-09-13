"""
@File   :   t_g_config.py
@Date   :   2024/6/10 01:01
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""


class TGConfig:
    def __init__(self, message='', code="0", show_tab=True, catch=True, toast="element", config_clear_token=False,description="no"):
        """

        :param message: 错误信息
        :param code: 错误码
        :param show_tab: 是否在拦截器层面显示交互反馈
        :param catch: 是否导致 进入catch
        :param toast: 错误提示方式
                        可选 element (elmessage)
                            uniapp(uni.showToast)
                            system(app-plus.nativeUI.toast other-uni.showToast)
        """
        try:
            from flask import g as fuck
            fuck.code = code
            fuck.message = message
            fuck.config_custom_showTab = show_tab
            fuck.config_custom_catch = catch
            fuck.config_toast_type = toast
            fuck.config_clear_token = config_clear_token
            fuck.description = description
        except Exception as e:
            print(f"flask 全局信息注册失败 ：{e}")
            ...
