FROM node:22-bookworm-slim AS node22
FROM node:20-bookworm-slim
ARG DSH_VERSION=0.1.7-rc.2
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-fusepy libfuse2 fuse util-linux git ca-certificates iptables \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g --no-fund --no-audit @deepseek-ai/dsh@${DSH_VERSION}
COPY --from=node22 /usr/local/bin/node /usr/local/bin/node
RUN apt-get update && apt-get install -y --no-install-recommends python3-pip python3-venv && rm -rf /var/lib/apt/lists/*
COPY quota_fs.py entrypoint.py model_proxy.py model_capacity.py net_proxy.py tls_guard.py freeze.py /opt/sandbox/
ENTRYPOINT ["/usr/bin/python3", "/opt/sandbox/entrypoint.py"]
