#!/usr/bin/env bash
# =============================================================================
# agent_ready.sh — 考勤系统 AGENT（DeepSeek）服务器环境自检 + 冒烟测试
# -----------------------------------------------------------------------------
# 用法：
#   1) 先把 API Key 配到环境变量（不要写进脚本/聊天/文档）：
#       临时：  export AGENT_API_KEY='sk-xxx'
#       永久：  sudo systemctl edit 你的服务名   → 加 Environment=AGENT_API_KEY=sk-xxx
#   2) 运行本脚本：
#       bash agent_ready.sh            （模型默认 deepseek-v4-flash）
#       AGENT_MODEL=deepseek-chat bash agent_ready.sh   （想换模型时）
#
# 输出约定：全程不回显 Key；任何可能含 Key 的输出都会打码成 ***。
# 退出码：0=全部通过；1=失败(有具体原因)；2=Key 未配置。
# =============================================================================
set -u

API_BASE="${API_BASE:-https://api.deepseek.com}"
MODEL="${AGENT_MODEL:-deepseek-v4-flash}"
KEY="${AGENT_API_KEY:-}"
MASK='***'

# Key 打码：管道里任何输出过一遍这个函数（Key 为空时原样通过）
sanitize() {
  if [ -n "$KEY" ]; then sed "s|${KEY}|${MASK}|g"; else cat; fi
}

echo "== AGENT 环境自检（DeepSeek）=="
echo "API  : $API_BASE"
echo "模型 : $MODEL"

# ---------- 0) 前置：curl ----------
if ! command -v curl >/dev/null 2>&1; then
  echo "[失败] 未安装 curl，请先执行: sudo apt install -y curl"
  exit 1
fi

# ---------- 1) Key 是否已配置 ----------
if [ -z "$KEY" ]; then
  echo "[失败] 未检测到 AGENT_API_KEY 环境变量。"
  echo "       请在服务器上配置后再运行，例如："
  echo "         export AGENT_API_KEY='sk-你的key'   # 当前 shell 临时"
  echo "         sudo systemctl edit 你的服务名       # 永久，加 Environment= 行"
  exit 2
fi
echo "[通过] AGENT_API_KEY 已配置（长度 ${#KEY}，内容不回显）"

# ---------- 2) 网络连通性（TCP 443） ----------
host=$(echo "$API_BASE" | sed -E 's|https?://([^/]+).*|\1|')
if timeout 8 bash -c "</dev/tcp/$host/443" 2>/dev/null; then
  echo "[通过] TCP 443 可达: $host"
else
  echo "[失败] TCP 443 不可达: $host —— 请检查防火墙/网络放行"
  exit 1
fi

# ---------- 3) 鉴权 + 模型列表 ----------
TMP=$(mktemp)
HTTP=$(curl -sS -o "$TMP" -w "%{http_code}" --max-time 20 \
        -H "Authorization: Bearer $KEY" \
        "$API_BASE/v1/models" 2>&1 | tail -1)
case "$HTTP" in
  200)
    echo "[通过] 鉴权成功（/v1/models → 200）"
    ;;
  401|403)
    echo "[失败] 鉴权被拒（HTTP $HTTP）—— Key 无效或已被重置，请到 platform.deepseek.com 重新生成"
    cat "$TMP" | sanitize; rm -f "$TMP"; exit 1
    ;;
  *)
    echo "[失败] 请求异常（HTTP ${HTTP:-000}）—— 网络/TLS 或服务端问题"
    cat "$TMP" | sanitize; rm -f "$TMP"; exit 1
    ;;
esac

# 列出平台实际可用的 deepseek* 模型 id
AVAILABLE=$(grep -o '"id":"[^"]*"' "$TMP" | sed 's/"id":"//;s/"//' | sort -u)
echo "平台模型列表中包含: $(echo "$AVAILABLE" | grep -i deepseek | tr '\n' ' ')"
if ! echo "$AVAILABLE" | grep -qx "$MODEL"; then
  echo "[警告] 目标模型 '$MODEL' 不在列表里 —— 模型 id 可能不同。"
  echo "       建议改用平台实际 id（常见: deepseek-chat）。可用:"
  echo "$AVAILABLE" | grep -i deepseek | sed 's/^/         - /'
  # 不退出：让第 4 步的调用给出权威报错（若 id 无效 API 会返回 400 说明）
fi
rm -f "$TMP"

# ---------- 4) function calling 冒烟测试 ----------
echo "== 冒烟测试：向 $MODEL 发一条带工具定义的对话 =="
PAYLOAD=$(cat <<EOF
{
  "model": "$MODEL",
  "messages": [{"role": "user", "content": "请调用 ping 工具并返回它的结果"}],
  "tools": [{
    "type": "function",
    "function": {
      "name": "ping",
      "description": "连通性测试工具，调用即返回 pong",
      "parameters": {"type": "object", "properties": {}}
    }
  }],
  "tool_choice": "auto",
  "max_tokens": 200
}
EOF
)

START=$(date +%s)
TMP=$(mktemp)
HTTP=$(curl -sS -o "$TMP" -w "%{http_code}" --max-time 90 \
        -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
        -d "$PAYLOAD" "$API_BASE/v1/chat/completions" 2>&1 | tail -1)
ELAPSED=$(( $(date +%s) - START ))

if [ "$HTTP" = "200" ]; then
  if grep -q '"tool_calls"' "$TMP" && grep -q '"name": *"ping"' "$TMP"; then
    echo "[通过] function calling 正常：模型按要求发起了 ping 工具调用（${ELAPSED}s）"
    # 回显用量（不含内容，避免把模型输出里的业务无关信息刷屏）
    grep -o '"usage":[^}]*}' "$TMP" | sanitize || true
    rm -f "$TMP"; exit 0
  else
    echo "[警告] HTTP 200 但响应里没有 tool_calls —— 该模型可能不支持 function calling，"
    echo "       或模型没按指令走工具。响应摘录："
    head -c 600 "$TMP" | sanitize
    rm -f "$TMP"; exit 1
  fi
else
  echo "[失败] 对话接口 HTTP ${HTTP:-000}（${ELAPSED}s）。响应："
  cat "$TMP" | sanitize
  rm -f "$TMP"
  echo ""
  echo "常见原因："
  echo "  - 模型 id 无效（报 model not found / 400）→ 用上一步列表里的真实 id 重跑："
  echo "      AGENT_MODEL=deepseek-chat bash agent_ready.sh"
  echo "  - Key 无效/额度不足（报 401 / insufficient）"
  echo "  - 网络问题（HTTP 000）"
  exit 1
fi
