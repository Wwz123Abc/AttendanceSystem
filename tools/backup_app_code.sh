#!/usr/bin/env bash
# 考勤系统「程序代码 + 配置」备份（2026-11-20 固定下来的部署备份方式）
#
# 只备份程序代码和 appsettings 等配置，不备份 logs / wwwroot / PrivateUploads：
#   - logs 每天写、备份时还在变；
#   - wwwroot、PrivateUploads 是员工照片/附件等，几个 G，和本次发布无关，备份一次要几分钟。
# 这样备份通常只有 40~50MB、几秒钟完成，部署不会再因为备份太大而超时。
#
# 用法（在服务器上执行）：
#   bash tools/backup_app_code.sh
# 可选参数：程序目录（默认 /var/www/attendance-net）、备份目录（默认 /root/att-backups/app）
set -euo pipefail

APP_DIR="${1:-/var/www/attendance-net}"
OUT_DIR="${2:-/root/att-backups/app}"
TS="$(date +%Y%m%d_%H%M%S)"

sudo mkdir -p "$OUT_DIR"
sudo tar czf "$OUT_DIR/att_${TS}_app_code.tar.gz" \
    --exclude=logs --exclude=wwwroot --exclude=PrivateUploads \
    -C "$APP_DIR" .

echo "已备份（代码+配置，不含 logs/wwwroot/PrivateUploads）：$OUT_DIR/att_${TS}_app_code.tar.gz"
