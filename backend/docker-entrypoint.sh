#!/bin/sh
# 容器入口：以 root 准备挂载目录的属主，然后降权为非 root 用户运行 API。
# Agent 运行时会在容器内执行命令（dsh / 终端），因此 API 进程与其子进程不应以 root 身份运行。
set -e

RUN_USER="${DSH_RUN_USER:-app}"
DATA_DIR="${Dsh__Storage__DataDir:-/app/data}"
LOG_DIR="${Dsh__Storage__LogDir:-/app/logs}"
CACHE_DIR="${Dsh__Storage__CacheDir:-/app/cache}"

mkdir -p "$DATA_DIR" "$LOG_DIR" "$CACHE_DIR" "$CACHE_DIR/home"

if [ "$(id -u)" = "0" ] && id "$RUN_USER" >/dev/null 2>&1; then
  RUN_UID="$(id -u "$RUN_USER")"
  # 仅当目录属主不对时才递归 chown，避免每次启动都遍历大目录
  for d in "$DATA_DIR" "$LOG_DIR" "$CACHE_DIR"; do
    if [ "$(stat -c %u "$d")" != "$RUN_UID" ]; then chown -R "$RUN_UID:$(id -g "$RUN_USER")" "$d"; fi
  done
  # dsh / npm 的 HOME 与缓存放到缓存卷，不写入容器可写层
  export HOME="$CACHE_DIR/home"
  chown "$RUN_UID:$(id -g "$RUN_USER")" "$HOME" 2>/dev/null || true
  exec setpriv --reuid="$RUN_UID" --regid="$(id -g "$RUN_USER")" --init-groups "$@"
fi

export HOME="${HOME:-$CACHE_DIR/home}"
exec "$@"
