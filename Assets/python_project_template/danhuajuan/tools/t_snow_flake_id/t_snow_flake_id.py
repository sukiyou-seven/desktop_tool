"""
@File   :   t_snow_flake_id.py
@Date   :   2024/5/28 14:01
@Author :   sukiyou
@Version:   0.0.1
@contact:   s2788349898@163.com
@License:   MIT
@Desc   :   wlp
"""
import time



class SnowFlakeId:
    def __init__(self, datacenter_id, worker_id, sequence=0):
        # 数据中心ID和工作机器ID的位数
        self.datacenter_id_bits = 5
        self.worker_id_bits = 5
        self.max_datacenter_id = -1 ^ (-1 << self.datacenter_id_bits)
        self.max_worker_id = -1 ^ (-1 << self.worker_id_bits)

        # 序列号的位数以及掩码（这里用-1的右移位操作生成掩码）
        self.sequence_bits = 12
        self.sequence_mask = -1 ^ (-1 << self.sequence_bits)

        # 时间戳的左移位数
        self.timestamp_left_shift = self.sequence_bits + self.worker_id_bits + self.datacenter_id_bits
        self.datacenter_id_shift = self.sequence_bits + self.worker_id_bits
        self.worker_id_shift = self.sequence_bits

        # 数据中心ID和工作机器ID的范围检查
        if datacenter_id > self.max_datacenter_id or datacenter_id < 0:
            raise ValueError("Datacenter ID can't be greater than %d or less than 0" % self.max_datacenter_id)
        if worker_id > self.max_worker_id or worker_id < 0:
            raise ValueError("Worker ID can't be greater than %d or less than 0" % self.max_worker_id)

        # 初始化属性
        self.datacenter_id = datacenter_id
        self.worker_id = worker_id
        self.sequence = sequence

        # 假定一个时间戳基准（这里是42年的时间 2022 - 1970 = 52年）
        self.epoch = 1640995200000

        # 最后生成ID的时间戳，初始值为负数
        self.last_timestamp = -1

    def _next_timestamp(self, last_timestamp):
        timestamp = int(time.time() * 1000)
        while timestamp <= last_timestamp:
            timestamp = int(time.time() * 1000)
        return timestamp

    def next_id(self):
        timestamp = int(time.time() * 1000)  # 获取当前的时间戳，单位是毫秒

        # 如果当前时间戳小于上一次ID生成的时间戳，表明系统时钟回退，抛出异常
        if timestamp < self.last_timestamp:
            raise Exception("Clock moved backwards. Refusing to generate id")

        # 如果是同一时间生成的，则进行毫秒内序列
        if self.last_timestamp == timestamp:
            self.sequence = (self.sequence + 1) & self.sequence_mask
            if self.sequence == 0:
                timestamp = self._next_timestamp(self.last_timestamp)  # 序列号等于0意味着毫秒内数值已经达到最大，等待下一个毫秒
        else:
            self.sequence = 0  # 时间戳改变，毫秒内序列重置

        self.last_timestamp = timestamp  # 更新上次生成ID的时间戳

        # 移位并通过或运算拼到一起组成64位的ID
        return ((timestamp - self.epoch) << self.timestamp_left_shift) | \
            (self.datacenter_id << self.datacenter_id_shift) | \
            (self.worker_id << self.worker_id_shift) | \
            self.sequence


class uniqueness_id:
    def __init__(self,prefix = "S_"):
        worker = SnowFlakeId(datacenter_id=1,
                             worker_id=1,
                             sequence=10)
        self.res = prefix + str(worker.next_id())

    def __del__(self):
        return self.res


if __name__ == '__main__':
    a = uniqueness_id("no_").res
    print(a)
    print(len(a))

