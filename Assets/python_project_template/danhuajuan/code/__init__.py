class Code:
    """状态码定义"""
    SUCCESS = 0             # 成功
    
    # 用户相关错误 (10000-10999)
    USER_NOT_EXIST = 10001   # 用户不存在
    USER_ALREADY_EXIST = 10002  # 用户已存在
    PASSWORD_ERROR = 10003    # 密码错误
    LOGIN_EXPIRED = 10004     # 登录过期
    TOKEN_INVALID = 10005     # token无效
    ACCOUNT_DISABLED = 10006  # 账号已禁用
    ACCOUNT_LOCKED = 10007    # 账号已锁定
    LOGIN_REQUIRED = 10008    # 需要登录
    LOGOUT_ERROR = 10009      # 登出失败
    
    # 权限相关错误 (11000-11999)
    NO_PERMISSION = 11001    # 没有权限
    ROLE_NOT_EXIST = 11002   # 角色不存在
    ACCESS_DENIED = 11003    # 拒绝访问
    INVALID_OPERATION = 11004 # 非法操作
    
    # 参数相关错误 (12000-12999)
    PARAM_ERROR = 12001      # 参数错误
    PARAM_MISSING = 12002    # 参数缺失
    PARAM_FORMAT_ERROR = 12003  # 参数格式错误
    PARAM_VALUE_ERROR = 12004   # 参数值错误
    
    # 业务操作错误 (13000-13999)
    OPERATION_FAILED = 13001  # 操作失败
    DATA_NOT_FOUND = 13002   # 数据不存在
    DATA_EXISTED = 13003     # 数据已存在
    DATA_ERROR = 13004       # 数据错误
    
    # 系统错误 (14000-14999)
    SYSTEM_ERROR = 14001     # 系统错误
    SERVICE_UNAVAILABLE = 14002  # 服务不可用
    TIMEOUT = 14003          # 超时
    UNKNOWN_ERROR = 14004    # 未知错误 