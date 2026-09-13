<?php
session_start();
$send_data = $_SESSION['api_response_json'] ?? null;
unset($_SESSION['api_response_json']);  // 清除，避免泄露
// 把PHP数据转为合法JSON，供前端调用
$apiJson = json_encode($send_data, JSON_PRETTY_PRINT, JSON_UNESCAPED_UNICODE);
//print_r($error_json);

require_once __DIR__. "/seraphine/config/read_config.php";


$app_config = ReadConfig::read_yml("app");


?>

<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <link>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>API 响应结果</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Roboto', 'Helvetica Neue', Arial, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
            position: relative;
            overflow-x: hidden;
        }

        body::before {
            content: '';
            position: absolute;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background:
                radial-gradient(circle at 20% 50%, rgba(255, 255, 255, 0.1) 0%, transparent 50%),
                radial-gradient(circle at 80% 80%, rgba(255, 255, 255, 0.1) 0%, transparent 50%);
            pointer-events: none;
        }

        .container {
            max-width: 1000px;
            width: 100%;
            background: rgba(255, 255, 255, 0.98);
            border-radius: 20px;
            box-shadow:
                0 25px 80px rgba(0, 0, 0, 0.25),
                0 0 0 1px rgba(255, 255, 255, 0.1);
            overflow: hidden;
            animation: slideUp 0.6s cubic-bezier(0.16, 1, 0.3, 1);
            backdrop-filter: blur(10px);
            position: relative;
            z-index: 1;
        }

        @keyframes slideUp {
            from {
                opacity: 0;
                transform: translateY(40px) scale(0.95);
            }
            to {
                opacity: 1;
                transform: translateY(0) scale(1);
            }
        }

        .header {
            background: linear-gradient(135deg, #10b981 0%, #059669 50%, #047857 100%);
            color: white;
            padding: 40px 30px;
            text-align: center;
            position: relative;
            overflow: hidden;
        }

        .header::before {
            content: '';
            position: absolute;
            top: -50%;
            left: -50%;
            width: 200%;
            height: 200%;
            background: radial-gradient(circle, rgba(255,255,255,0.1) 0%, transparent 70%);
            animation: shimmer 3s ease-in-out infinite;
        }

        @keyframes shimmer {
            0%, 100% { transform: translate(-30%, -30%) rotate(0deg); }
            50% { transform: translate(30%, 30%) rotate(180deg); }
        }

        .success-icon {
            font-size: 90px;
            margin-bottom: 20px;
            animation: successPulse 2.5s ease-in-out infinite;
            display: inline-block;
            filter: drop-shadow(0 8px 20px rgba(0, 0, 0, 0.2));
        }

        @keyframes successPulse {
            0%, 100% {
                transform: scale(1) rotate(0deg);
            }
            50% {
                transform: scale(1.15) rotate(5deg);
            }
        }

        .header h1 {
            font-size: 32px;
            margin-bottom: 12px;
            font-weight: 700;
            letter-spacing: 1px;
            text-shadow: 0 2px 10px rgba(0, 0, 0, 0.1);
            position: relative;
            z-index: 1;
        }

        .header p {
            opacity: 0.95;
            font-size: 17px;
            font-weight: 400;
            position: relative;
            z-index: 1;
        }

        .status-bar {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 20px;
            background: linear-gradient(to right, #f0fdf4, #ecfdf5);
            padding: 25px 30px;
            border-bottom: 2px solid #d1fae5;
        }

        .status-item {
            text-align: center;
            padding: 15px;
            background: white;
            border-radius: 12px;
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.05);
            transition: all 0.3s ease;
        }

        .status-item:hover {
            transform: translateY(-3px);
            box-shadow: 0 6px 20px rgba(0, 0, 0, 0.1);
        }

        .status-label {
            font-size: 11px;
            color: #6b7280;
            text-transform: uppercase;
            letter-spacing: 1.5px;
            margin-bottom: 8px;
            font-weight: 600;
        }

        .status-value {
            font-size: 22px;
            font-weight: 700;
            color: #10b981;
            word-break: break-all;
        }

        .status-value.code {
            color: #3b82f6;
            font-family: 'Courier New', monospace;
        }
        .status-value.dfalse {
            color: #ff5555;
            /*font-family: 'Courier New', monospace;*/
            font-weight: 900;
        }

        .content {
            padding: 35px;
        }

        .section-title {
            font-size: 20px;
            color: #1f2937;
            margin-bottom: 18px;
            display: flex;
            align-items: center;
            gap: 12px;
            font-weight: 600;
        }

        .section-title::before {
            content: "▸";
            color: #10b981;
            font-size: 24px;
            animation: bounce 2s ease-in-out infinite;
        }

        @keyframes bounce {
            0%, 100% { transform: translateX(0); }
            50% { transform: translateX(5px); }
        }

        .json-container {
            background: linear-gradient(135deg, #1e293b 0%, #0f172a 100%);
            border-radius: 16px;
            padding: 25px;
            overflow-x: auto;
            position: relative;
            box-shadow:
                inset 0 2px 10px rgba(0, 0, 0, 0.3),
                0 4px 15px rgba(0, 0, 0, 0.1);
            border: 1px solid #334155;
        }

        .json-container pre {
            color: #e2e8f0;
            font-family: 'Fira Code', 'JetBrains Mono', 'Courier New', 'Monaco', monospace;
            font-size: 14px;
            line-height: 1.9;
            margin: 0;
            white-space: pre-wrap;
            word-wrap: break-word;
            text-shadow: 0 0 5px rgba(226, 232, 240, 0.1);
        }

        .copy-btn {
            position: absolute;
            top: 15px;
            right: 15px;
            background: rgba(255, 255, 255, 0.1);
            border: 1px solid rgba(255, 255, 255, 0.2);
            color: white;
            padding: 10px 18px;
            border-radius: 10px;
            cursor: pointer;
            font-size: 14px;
            font-weight: 500;
            transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1);
            backdrop-filter: blur(10px);
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.2);
        }

        .copy-btn:hover {
            background: rgba(255, 255, 255, 0.2);
            border-color: rgba(255, 255, 255, 0.4);
            transform: translateY(-2px);
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
        }

        .copy-btn.copied {
            background: linear-gradient(135deg, #10b981, #059669);
            border-color: #10b981;
            box-shadow: 0 4px 15px rgba(16, 185, 129, 0.4);
        }

        .data-preview {
            margin-top: 30px;
            padding: 25px;
            background: linear-gradient(to right, #f9fafb, #f3f4f6);
            border-radius: 16px;
            border-left: 5px solid #10b981;
            box-shadow: 0 2px 10px rgba(0, 0, 0, 0.05);
            transition: all 0.3s ease;
        }

        .data-preview:hover {
            box-shadow: 0 4px 20px rgba(0, 0, 0, 0.08);
        }

        .data-preview h3 {
            color: #1f2937;
            margin-bottom: 15px;
            font-size: 18px;
            font-weight: 600;
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .data-type {
            display: inline-block;
            padding: 5px 14px;
            background: linear-gradient(135deg, #dbeafe, #bfdbfe);
            color: #2563eb;
            border-radius: 20px;
            font-size: 12px;
            font-weight: 700;
            box-shadow: 0 2px 6px rgba(59, 130, 246, 0.2);
        }

        .empty-data {
            color: #9ca3af;
            font-style: italic;
            padding: 25px;
            text-align: center;
            background: white;
            border-radius: 10px;
            border: 2px dashed #e5e7eb;
        }

        .array-item {
            background: white;
            padding: 16px;
            margin-bottom: 12px;
            border-radius: 10px;
            border-left: 4px solid #10b981;
            transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1);
            box-shadow: 0 2px 6px rgba(0, 0, 0, 0.05);
        }

        .array-item:hover {
            transform: translateX(8px);
            box-shadow: 0 6px 20px rgba(0, 0, 0, 0.1);
            border-left-color: #059669;
        }

        .timestamp {
            text-align: center;
            padding: 25px;
            color: #6b7280;
            font-size: 14px;
            border-top: 1px solid #e5e7eb;
            background: linear-gradient(to right, #f9fafb, #f3f4f6);
            font-weight: 500;
        }

        /* JSON格式化核心样式 */
        .json-property {
            margin-left: 20px;
            padding: 2px 0;
        }

        .json-toggle {
            cursor: pointer;
            user-select: none;
            color: #94a3b8;
            display: inline-block;
            width: 20px;
            transition: all 0.2s ease;
        }

        .json-toggle:hover {
            color: #cbd5e1;
            transform: scale(1.1);
        }

        .json-toggle.collapsed::before {
            content: "▼ ";
            font-size: 10px;
        }

        .json-toggle.expanded::before {
            content: "▶ ";
            font-size: 10px;
        }

        .json-key {
            color: #ff79c6;
            font-weight: 600;
        }

        .json-string {
            color: #f1fa8c;
        }

        .json-number {
            color: #bd93f9;
            font-weight: 600;
        }

        .json-boolean {
            color: #bd93f9;
            font-weight: 600;
        }

        .json-null {
            color: #ff5555;
            font-weight: 600;
        }

        .collapsed-content {
            display: none;
        }

        .json-collapsible {
            transition: all 0.3s ease;
        }

        @media (max-width: 768px) {
            body {
                padding: 10px;
            }

            .container {
                border-radius: 16px;
            }

            .header {
                padding: 30px 20px;
            }

            .success-icon {
                font-size: 70px;
            }

            .header h1 {
                font-size: 26px;
            }

            .header p {
                font-size: 15px;
            }

            .status-bar {
                grid-template-columns: 1fr;
                gap: 15px;
                padding: 20px;
            }

            .content {
                padding: 25px 20px;
            }

            .json-container {
                padding: 20px 15px;
            }

            .json-container pre {
                font-size: 12px;
                line-height: 1.7;
            }

            .copy-btn {
                top: 10px;
                right: 10px;
                padding: 8px 14px;
                font-size: 13px;
            }

            .data-preview {
                padding: 20px 15px;
            }

            .array-item {
                padding: 12px;
            }
        }

        @media (max-width: 480px) {
            .header h1 {
                font-size: 22px;
            }

            .status-value {
                font-size: 18px;
            }

            .section-title {
                font-size: 17px;
            }
        }

        .json-container pre[class*="language-"] {
            background: transparent !important;
            color: #e2e8f0 !important;
            text-shadow: none !important;
        }

        ::-webkit-scrollbar {
            width: 10px;
            height: 10px;
        }

        ::-webkit-scrollbar-track {
            background: #0f172a;
            border-radius: 5px;
        }

        ::-webkit-scrollbar-thumb {
            background: linear-gradient(135deg, #64748b, #475569);
            border-radius: 5px;
        }

        ::-webkit-scrollbar-thumb:hover {
            background: linear-gradient(135deg, #94a3b8, #64748b);
        }
    </style>
</head>
<body>
<div class="container">
    <div class="header">
        <div class="success-icon">✅</div>
        <h1>请求成功</h1>
        <p>API 响应结果</p>
        <p class="status-value dfalse">
            <?php echo $app_config['app']['debug']? 'DEBUG 已开启 生产模式记得关闭' : '已关闭'; ?></p>
    </div>

    <div class="status-bar">
        <div class="status-item">
            <div class="status-label">状态码</div>
            <div class="status-value code" id="statusCode">-5377</div>
        </div>
        <div class="status-item">
            <div class="status-label">消息</div>
            <div class="status-value" id="statusMessage">未能获取</div>
        </div>
        <div class="status-item">
            <div class="status-label">数据类型</div>
            <div class="status-value" id="dataType">未能获取</div>
        </div>
        <div class="status-item">
            <div class="status-label">DEBUG</div>
            <div class="status-value <?php echo $app_config['app']['debug']? 'dfalse' : ''; ?>" id="dataType">
                <?php echo $app_config['app']['debug']? '已开启' : '已关闭'; ?>
            </div>
        </div>
    </div>

    <div class="content">
        <div class="section-title">JSON 响应</div>
        <div class="json-container">
            <button class="copy-btn" onclick="copyJson()">📋 复制</button>
            <pre id="json-output"></pre>
        </div>

        <div class="data-preview" id="dataPreview" style="display: none;">
            <h3>数据预览 <span class="data-type" id="dataBadge"></span></h3>
            <div id="dataContent"></div>
        </div>
    </div>

    <div class="timestamp" id="timestamp"></div>
</div>

<script>
    // 传入后端生成的合法JSON
    const apiData = <?php echo $apiJson; ?>;
    const rawJson = <?php echo $apiJson; ?>;

    //填充顶部状态信息
    if (apiData && typeof apiData === 'object') {
        document.getElementById('statusCode').textContent = apiData.code ?? (apiData.status ?? '0');
        document.getElementById('statusMessage').textContent = apiData.message ?? (apiData.msg ?? 'success');
    }
    // 填充时间戳
    document.getElementById('timestamp').textContent = '请求时间：' + new Date().toLocaleString();

    // ========== 核心：JSON格式化折叠高亮，实现你截图效果 ==========
    function syntaxHighlightJSON(obj, container) {
        function processValue(value) {
            if (typeof value === 'string') {
                return `<span class="json-string">"${escapeHtml(value)}"</span>`;
            } else if (typeof value === 'number') {
                return `<span class="json-number">${value}</span>`;
            } else if (typeof value === 'boolean') {
                return `<span class="json-boolean">${value}</span>`;
            } else if (value === null) {
                return `<span class="json-null">null</span>`;
            } else if (Array.isArray(value)) {
                if (value.length === 0) return "[]";
                let isEmpty = value.length === 0;
                let html = `<span class="json-toggle collapsed" onclick="toggleCollapse(this)"></span>[<div class="json-collapsible">`;
                for (let i = 0; i < value.length; i++) {
                    html += `<div class="json-property">${processValue(value[i])}${i < value.length - 1 ? ',' : ''}</div>`;
                }
                html += `</div>]`;
                return html;
            } else if (typeof value === 'object') {
                let keys = Object.keys(value);
                if (keys.length === 0) return "{}";
                let html = `<span class="json-toggle collapsed" onclick="toggleCollapse(this)"></span>{<div class="json-collapsible">`;
                keys.forEach((key, idx) => {
                    html += `<div class="json-property"><span class="json-key">"${escapeHtml(key)}"</span>: ${processValue(value[key])}${idx < keys.length - 1 ? ',' : ''}</div>`;
                });
                html += `</div>}`;
                return html;
            }
        }

        container.innerHTML = processValue(obj);
    }

    // 切换折叠展开
    function toggleCollapse(toggle) {
        const parent = toggle.parentElement;
        const content = parent.querySelector('.json-collapsible');
        if (toggle.classList.contains('collapsed')) {
            toggle.classList.remove('collapsed');
            toggle.classList.add('expanded');
            content.classList.add('collapsed-content');
        } else {
            toggle.classList.remove('expanded');
            toggle.classList.add('collapsed');
            content.classList.remove('collapsed-content');
        }
    }

    // HTML转义，避免注入和渲染错误
    function escapeHtml(text) {
        return text
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }

    // 初始化渲染JSON
    syntaxHighlightJSON(apiData, document.getElementById('json-output'));

    // 渲染数据预览
    renderDataPreview(apiData);

    function renderDataPreview(data) {
        const preview = document.getElementById('dataPreview');
        const content = document.getElementById('dataContent');
        const badge = document.getElementById('dataBadge');
        const dataTypeEl = document.getElementById('dataType');

        if (data === null || data === undefined) {
            preview.style.display = 'block';
            badge.textContent = 'NULL';
            content.innerHTML = '<div class="empty-data">数据为空</div>';
            dataTypeEl.textContent = 'Null';
            return;
        }

        const type = typeof data;

        if (type === 'string') {
            preview.style.display = 'block';
            badge.textContent = 'String';
            content.innerHTML = `<div style="color: #10b981; font-family: monospace;">"${data}"</div>`;
            dataTypeEl.textContent = 'String';
        } else if (Array.isArray(data)) {
            preview.style.display = 'block';
            badge.textContent = `Array (${data.length})`;
            dataTypeEl.textContent = 'Array';

            if (data.length === 0) {
                content.innerHTML = '<div class="empty-data">空数组</div>';
            } else {
                content.innerHTML = data.map((item, index) =>
                    `<div class="array-item">
                    <strong>[${index}]</strong> ${typeof item === 'object' ? JSON.stringify(item, null, 2) : item}
                </div>`
                ).join('');
            }
        } else if (type === 'object') {
            preview.style.display = 'block';
            const keys = Object.keys(data);
            badge.textContent = `Object (${keys.length})`;
            dataTypeEl.textContent = 'Object';

            if (keys.length === 0) {
                content.innerHTML = '<div class="empty-data">空对象 {}</div>';
            } else {
                content.innerHTML = keys.map(key =>
                    `<div class="array-item">
                    <strong style="color: #3b82f6;">${key}:</strong>
                    <span style="color: #10b981;">${typeof data[key] === 'object' ? JSON.stringify(data[key], null, 2) : data[key]}</span>
                </div>`
                ).join('');
            }
        } else {
            preview.style.display = 'block';
            badge.textContent = type.charAt(0).toUpperCase() + type.slice(1);
            content.innerHTML = `<div style="color: #10b981;">${data}</div>`;
            dataTypeEl.textContent = type.charAt(0).toUpperCase() + type.slice(1);
        }
    }

    // 复制JSON
    function copyJson() {
        const jsonStr = JSON.stringify(rawJson, null, 2);
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(jsonStr).then(() => {
                const btn = document.querySelector('.copy-btn');
                btn.textContent = '✅ 已复制';
                btn.classList.add('copied');
                setTimeout(() => {
                    btn.textContent = '📋 复制';
                    btn.classList.remove('copied');
                }, 2000);
            });
        } else {
            // 降级方案
            const textarea = document.createElement('textarea');
            textarea.value = jsonStr;
            document.body.appendChild(textarea);
            textarea.select();
            document.execCommand('copy');
            document.body.removeChild(textarea);

            const btn = document.querySelector('.copy-btn');
            btn.textContent = '✅ 已复制';
            btn.classList.add('copied');
            setTimeout(() => {
                btn.textContent = '📋 复制';
                btn.classList.remove('copied');
            }, 2000);
        }
    }
</script>
</body>
</html>
