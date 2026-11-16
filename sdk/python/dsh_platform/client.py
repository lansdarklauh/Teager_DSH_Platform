# -*- coding: utf-8 -*-
"""DSH 平台 Python SDK（仅依赖标准库）。

所有请求走统一的平台 API：``Authorization: Bearer dshp_xxx``。外部客户端永远不直接依赖 DSH 原生 API，
也不需要知道 Runtime 的地址、端口或 Launch Token。

示例::

    from dsh_platform import Client

    client = Client(base_url="https://dsh.company.com", token="dshp_xxxxx")
    workspace = client.workspace("ws_123")
    session = workspace.create_session()
    result = session.send("分析当前项目并找出问题")
    print(result.text)
    session.send("把这些问题修复")
"""
from __future__ import annotations

import json
import mimetypes
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass, field
from typing import Any, Iterator, Optional

__all__ = ["Client", "Workspace", "Session", "SharedSpace", "Result", "DshError"]


class DshError(Exception):
    """平台返回的错误：包含 HTTP 状态码、错误码与中文错误信息。"""

    def __init__(self, status: int, code: str, message: str):
        super().__init__(f"[{status}] {code}: {message}")
        self.status = status
        self.code = code
        self.message = message


@dataclass
class Result:
    """一次 ``session.send`` 的结果。"""

    text: str
    status: str
    run_id: str
    input_tokens: int = 0
    output_tokens: int = 0
    duration_ms: int = 0
    error: Optional[str] = None
    raw: dict = field(default_factory=dict, repr=False)

    @property
    def ok(self) -> bool:
        return self.status == "Completed"


class Client:
    """平台客户端。

    :param base_url: 平台地址，如 ``https://dsh.company.com``（不含 /api/v1）。
    :param token: Bearer Token（``dshp_`` 开头）。
    :param timeout: 单次 HTTP 请求超时（秒）；同步等待运行结果时会按需放宽。
    """

    def __init__(self, base_url: str, token: str, timeout: float = 60.0):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.timeout = timeout

    # ── 底层请求 ──
    def _request(self, method: str, path: str, body: Any = None, params: Optional[dict] = None,
                 raw: bool = False, timeout: Optional[float] = None, headers: Optional[dict] = None,
                 data: Optional[bytes] = None):
        url = f"{self.base_url}/api/v1{path}"
        if params:
            url += "?" + urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})
        h = {"Authorization": f"Bearer {self.token}", "Accept": "application/json"}
        if headers:
            h.update(headers)
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            h["Content-Type"] = "application/json"
        req = urllib.request.Request(url, data=data, method=method, headers=h)
        try:
            with urllib.request.urlopen(req, timeout=timeout or self.timeout) as r:
                payload = r.read()
                if raw:
                    return payload
                return json.loads(payload) if payload else None
        except urllib.error.HTTPError as e:
            payload = e.read()
            try:
                info = json.loads(payload)
                raise DshError(e.code, info.get("code", "error"), info.get("message", "请求失败")) from None
            except (ValueError, AttributeError):
                raise DshError(e.code, "error", payload.decode("utf-8", "replace")[:300]) from None

    # ── 入口对象 ──
    def me(self) -> dict:
        """当前身份（用户、有效 Pool）。"""
        return self._request("GET", "/auth/me")

    def meta(self) -> dict:
        """平台元信息（版本、Scope 清单等）。"""
        return self._request("GET", "/meta")

    def pools(self) -> list:
        """可见的 Pool 列表（扁平，含 parentPoolId）。"""
        return self._request("GET", "/pools")

    def workspaces(self, pool_id: Optional[str] = None, include_descendants: bool = False) -> list:
        """可访问的 Workspace 列表。"""
        return self._request("GET", "/workspaces", params={"poolId": pool_id, "includeDescendants": "true" if include_descendants else None})

    def workspace(self, workspace_id: str) -> "Workspace":
        """获取 Workspace 句柄（不发请求）。"""
        return Workspace(self, workspace_id)

    def session(self, session_id: str) -> "Session":
        """获取已有会话句柄。"""
        return Session(self, session_id)

    def shared_space(self, space_id: str) -> "SharedSpace":
        """获取共享空间句柄。"""
        return SharedSpace(self, space_id)

    def shared_spaces(self) -> list:
        """可访问的共享空间列表。"""
        return self._request("GET", "/shared-spaces")

    def search_sessions(self, keyword: str) -> list:
        """跨 Workspace 搜索会话（标题 / 内容）。"""
        return self._request("GET", "/sessions/search", params={"q": keyword})


class _Files:
    """文件操作（Workspace 私有文件、挂载的共享空间或直接访问共享空间）。"""

    def __init__(self, client: Client, base: str, area: Optional[str] = None):
        self._c, self._base, self._area = client, base, area

    def _p(self, **extra) -> dict:
        d = {"area": self._area}
        d.update(extra)
        return d

    def list(self, path: str = "") -> list:
        """列出目录。"""
        return self._c._request("GET", f"{self._base}/files", params=self._p(path=path))["entries"]

    def read(self, path: str) -> str:
        """读取文本文件。"""
        return self._c._request("GET", f"{self._base}/files/content", params=self._p(path=path))["content"]

    def write(self, path: str, content: str) -> None:
        """写入（覆盖）文本文件。"""
        self._c._request("PUT", f"{self._base}/files/content", body={"path": path, "content": content}, params=self._p())

    def mkdir(self, path: str) -> None:
        """创建目录。"""
        self._c._request("POST", f"{self._base}/files/mkdir", body={"path": path}, params=self._p())

    def delete(self, path: str) -> None:
        """删除文件或目录。"""
        self._c._request("DELETE", f"{self._base}/files", params=self._p(path=path))

    def move(self, src: str, dst: str) -> None:
        """重命名 / 移动。"""
        self._c._request("POST", f"{self._base}/files/move", body={"from": src, "to": dst}, params=self._p())

    def download(self, path: str) -> bytes:
        """下载文件内容。"""
        return self._c._request("GET", f"{self._base}/files/download", params=self._p(path=path), raw=True, timeout=600)

    def upload(self, local_path: str, remote_dir: str = "") -> list:
        """上传本地文件到目录。"""
        import os
        name = os.path.basename(local_path)
        with open(local_path, "rb") as f:
            content = f.read()
        boundary = "----dsh" + uuid.uuid4().hex
        ctype = mimetypes.guess_type(name)[0] or "application/octet-stream"
        body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"files\"; filename=\"{name}\"\r\nContent-Type: {ctype}\r\n\r\n").encode() + content + f"\r\n--{boundary}--\r\n".encode()
        r = self._c._request("POST", f"{self._base}/files/upload", params=self._p(path=remote_dir), data=body, timeout=600,
                             headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
        return r["saved"]


class Workspace:
    """Workspace 句柄：创建会话、文件、共享挂载信息。"""

    def __init__(self, client: Client, workspace_id: str):
        self._c = client
        self.id = workspace_id

    def info(self) -> dict:
        """Workspace 详情。"""
        return self._c._request("GET", f"/workspaces/{self.id}")

    def create_session(self, title: Optional[str] = None, model: Optional[str] = None) -> "Session":
        """创建会话。"""
        s = self._c._request("POST", "/sessions", body={"workspaceId": self.id, "title": title, "model": model})
        return Session(self._c, s["id"])

    def sessions(self, keyword: Optional[str] = None) -> list:
        """会话列表。"""
        return self._c._request("GET", "/sessions", params={"workspaceId": self.id, "q": keyword})

    def mounts(self) -> dict:
        """共享空间挂载情况（含有效权限与来源）。"""
        return self._c._request("GET", f"/workspaces/{self.id}/mounts")

    def explain(self, shared_space_id: Optional[str] = None) -> dict:
        """权限来源解释。"""
        return self._c._request("GET", f"/workspaces/{self.id}/permissions/explain", params={"sharedSpaceId": shared_space_id})

    @property
    def files(self) -> _Files:
        """私有文件（files/）。"""
        return _Files(self._c, f"/workspaces/{self.id}")

    def shared_files(self, mount_name: str) -> _Files:
        """已挂载共享空间中的文件（shared/<挂载名>）。"""
        return _Files(self._c, f"/workspaces/{self.id}", area=f"shared/{mount_name}")


class SharedSpace:
    """共享空间句柄。"""

    def __init__(self, client: Client, space_id: str):
        self._c = client
        self.id = space_id

    def info(self) -> dict:
        return self._c._request("GET", f"/shared-spaces/{self.id}")

    def grants(self) -> list:
        return self._c._request("GET", f"/shared-spaces/{self.id}/grants")

    @property
    def files(self) -> _Files:
        return _Files(self._c, f"/shared-spaces/{self.id}")


class Session:
    """会话句柄：发送消息（同步 / 流式）、读取消息、取消。"""

    def __init__(self, client: Client, session_id: str):
        self._c = client
        self.id = session_id

    def send(self, content: str, model: Optional[str] = None, timeout: float = 1900.0, request_id: Optional[str] = None, project_ids: Optional[list[str]] = None) -> Result:
        """发送消息并同步等待最终结果（对应 ``POST /sessions/{id}/messages?wait=true``）。"""
        r = self._c._request("POST", f"/sessions/{self.id}/messages", body={"content": content, "model": model, "requestId": request_id or str(uuid.uuid4()), "projectIds": project_ids}, params={"wait": "true"}, timeout=timeout)
        return Result(text=r.get("text") or "", status=r.get("status") or "", run_id=r.get("runId"), input_tokens=r.get("inputTokens") or 0,
                      output_tokens=r.get("outputTokens") or 0, duration_ms=r.get("durationMs") or 0, error=r.get("error"), raw=r)

    def stream(self, content: str, model: Optional[str] = None, timeout: float = 1900.0, project_ids: Optional[list[str]] = None) -> Iterator[dict]:
        """发送消息并流式返回事件（SSE）：事件含 type = text / thinking / tool_call / tool_result / status / final / run_end 等。"""
        after = self._c._request("GET", f"/sessions/{self.id}")["lastEventSeq"]
        result = self._c._request("POST", f"/sessions/{self.id}/messages", body={"content": content, "model": model, "requestId": str(uuid.uuid4()), "projectIds": project_ids})
        yield from self.events(after=after, timeout=timeout, run_id=result['runId'])

    def events(self, after: int = 0, timeout: float = 1900.0, run_id: Optional[str] = None) -> Iterator[dict]:
        """订阅事件；按 seq 去重，指定运行时断线续接并过滤其他运行。"""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            url = f"{self._c.base_url}/api/v1/sessions/{self.id}/events?after={after}"
            req = urllib.request.Request(url, headers={"Authorization": f"Bearer {self._c.token}", "Accept": "text/event-stream"})
            try:
                with urllib.request.urlopen(req, timeout=min(60, max(.1, deadline - time.monotonic()))) as r:
                    data: list = []
                    for raw in r:
                        if time.monotonic() >= deadline:
                            raise DshError(504, "stream_timeout", "等待事件超时，任务仍可在后台运行")
                        line = raw.decode("utf-8").rstrip("\r\n")
                        if line.startswith("data: "):
                            data.append(line[6:])
                        elif line == "" and data:
                            event = json.loads("\n".join(data)); data = []
                            sequence = event.get('seq', 0)
                            if sequence <= after:
                                continue
                            after = sequence
                            if run_id and event.get('runId') != run_id:
                                continue
                            yield event
                            if event.get('type') == 'run_end':
                                return
                if run_id is None:
                    return
            except urllib.error.HTTPError as e:
                raise DshError(e.code, "error", e.read().decode("utf-8", "replace")[:300]) from None
            except (urllib.error.URLError, TimeoutError, ConnectionError, OSError):
                if run_id is None:
                    raise
            time.sleep(min(.5, max(0, deadline - time.monotonic())))
        raise DshError(504, "stream_timeout", "等待事件超时，任务仍可在后台运行")

    def messages(self) -> list:
        """消息列表，按游标读取完整历史（文本摘要）。"""
        rows, after = [], 0
        while True:
            page = self._c._request("GET", f"/sessions/{self.id}/messages", params={"after": after, "limit": 500})
            rows.extend(page)
            if len(page) < 500:
                return rows
            after = page[-1]['id']

    def cancel(self) -> bool:
        """取消正在运行的任务。"""
        return bool(self._c._request("POST", f"/sessions/{self.id}/cancel", body={})["cancelled"])

    def rename(self, title: str) -> None:
        self._c._request("PATCH", f"/sessions/{self.id}", body={"title": title})

    def delete(self) -> None:
        self._c._request("DELETE", f"/sessions/{self.id}")

    def export(self, fmt: str = "md") -> str:
        """导出会话为 Markdown / JSON 文本。"""
        return self._c._request("GET", f"/sessions/{self.id}/export", params={"format": fmt}, raw=True).decode("utf-8")
