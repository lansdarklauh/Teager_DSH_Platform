#!/bin/sh
# 卷挂载后宿主机空目录没有 nginx 默认子目录，启动前补齐，避免写回镜像层
mkdir -p /var/log/nginx \
  /var/cache/nginx/client_temp \
  /var/cache/nginx/proxy_temp \
  /var/cache/nginx/fastcgi_temp \
  /var/cache/nginx/uwsgi_temp \
  /var/cache/nginx/scgi_temp
chown -R nginx:nginx /var/log/nginx /var/cache/nginx
