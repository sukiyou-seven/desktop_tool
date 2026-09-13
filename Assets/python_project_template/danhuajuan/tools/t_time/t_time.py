"""
@File   :   t_time.py
@Date   :   2024/5/28 13:59
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import datetime
import time

import sxtwl


class TTime:
    def __init__(self, timestamp):
        self.__dt = datetime.datetime.fromtimestamp(int(timestamp))
        self.result = str(self.__dt.strftime('%Y-%m-%d %H:%M:%S'))

    def onlyDate(self):
        self.result = str(self.__dt.strftime('%Y-%m-%d'))
        return self.result

    def get_shi_zhi(self, hour):
        """根据小时获取时辰名称 + 地支下标"""

        # 十二时辰对应小时范围
        TIME_ZONE = [
            ("子时", 23, 1),
            ("丑时", 1, 3),
            ("寅时", 3, 5),
            ("卯时", 5, 7),
            ("辰时", 7, 9),
            ("巳时", 9, 11),
            ("午时", 11, 13),
            ("未时", 13, 15),
            ("申时", 15, 17),
            ("酉时", 17, 19),
            ("戌时", 19, 21),
            ("亥时", 21, 23),
        ]
        for idx, (name, start, end) in enumerate(TIME_ZONE):
            if start < end:
                if start <= hour < end:
                    return name, idx
            else:
                # 子时 23点~次日1点
                if hour >= start or hour < end:
                    return name, idx
        return "子时", 0

    def get_bazi(self):
        """
        公历年月日时
        :param gy: 年
        :param gm: 月
        :param gd: 日
        :param gh: 小时 0~23
        :return: 八字字符串、农历日期
        """

        nyr_sfm = self.result.split(" ")
        year = int(nyr_sfm[0].split("-")[0])
        month = int(nyr_sfm[0].split("-")[1])
        day = int(nyr_sfm[0].split("-")[2])
        hour = int(nyr_sfm[1].split(":")[0])

        gy = year
        gm = month
        gd = day
        gh = hour

        # 基础对照表
        GAN = ["甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸"]
        ZHI = ["子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥"]

        day = sxtwl.fromSolar(gy, gm, gd)

        # 年柱
        y_gz = day.getYearGZ()
        year = f"{GAN[y_gz.tg]}{ZHI[y_gz.dz]}"

        # 月柱
        m_gz = day.getMonthGZ()
        month = f"{GAN[m_gz.tg]}{ZHI[m_gz.dz]}"

        # 日柱
        d_gz = day.getDayGZ()
        day_gz = f"{GAN[d_gz.tg]}{ZHI[d_gz.dz]}"

        # 时柱
        shi_name, shi_idx = self.get_shi_zhi(gh)
        shi_gz = day.getHourGZ(shi_idx)
        shi = f"{GAN[shi_gz.tg]}{ZHI[shi_gz.dz]}"

        # 农历日期
        lunar_y = day.getLunarYear()
        lunar_m = day.getLunarMonth()
        lunar_d = day.getLunarDay()
        lunar_str = f"农历{lunar_y}年{lunar_m}月{lunar_d}"

        bazi = f"{year}年 {month}月 {day_gz}日 {shi}时（{shi_name}）"
        return bazi, lunar_str

