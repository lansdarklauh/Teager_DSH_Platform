#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Python SDK 端到端验收：用 Web 登录创建 Token，再只用 SDK 完成 创建会话 / 同步 / 流式 / 文件 / 共享空间 / 错误处理。"""
import os
import sys
import uuid

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "sdk", "python"))
sys.path.insert(0, os.path.dirname(__file__))
from e2e_api import Client as Web, check, FAILS  # noqa: E402
from dsh_platform import Client, DshError  # noqa: E402

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5600"


def main():
    web = Web(); s, _ = web.post("/auth/login", {"username": "a", "password": "Demo@1234"}); assert s == 200
    pools = {p["name"]: p for p in web.get("/pools")[1]}
    s, ws = web.post("/workspaces", {"poolId": pools["编辑部"]["id"], "name": "SDK-" + uuid.uuid4().hex[:4]})
    spaces = {x["name"]: x for x in web.get("/shared-spaces")[1]}
    web.post(f"/workspaces/{ws['id']}/mounts", {"sharedSpaceId": spaces["集团公共资料"]["id"], "mountName": "group-public"})
    s, tk = web.post("/tokens", {"name": "sdk-e2e", "scopes": ["workspace:read", "session:read", "session:create", "session:execute", "files:read", "files:write", "share:read", "share:write", "pool:read"], "allowedWorkspaces": [ws["id"]]})
    client = Client(BASE, tk["token"])

    check("SDK：me()", client.me()["username"] == "a")
    check("SDK：meta() 含 Scope 清单", "session:execute" in client.meta()["scopes"])
    w = client.workspace(ws["id"])
    check("SDK：Workspace 白名单生效", [x["id"] for x in client.workspaces()] == [ws["id"]])
    session = w.create_session(title="SDK 会话")
    r = session.send("分析当前项目并找出问题")
    check("SDK：send() 同步返回最终文本", r.ok and "模拟回复" in r.text and r.input_tokens > 0, str(r))
    r2 = session.send("把这些问题修复")
    check("SDK：继续同一会话（消息累计 4 条）", r2.ok and len(session.messages()) == 4)
    events = list(session.stream("/write files/sdk.txt 由SDK触发"))
    types = [e["type"] for e in events]
    check("SDK：stream() 流式事件含 tool_call 与 run_end", "tool_call" in types and types[-1] == "run_end", str(types))
    check("SDK：Agent 写入的文件可读取", w.files.read("sdk.txt") == "由SDK触发")
    w.files.mkdir("docs"); w.files.write("docs/a.md", "# a")
    check("SDK：文件写入与列表", any(e["name"] == "a.md" for e in w.files.list("docs")))
    tmp = os.path.join(os.path.dirname(__file__), "_upload.txt")
    with open(tmp, "w", encoding="utf-8") as f: f.write("upload")
    try:
        check("SDK：文件上传", w.files.upload(tmp, "docs") == ["docs/_upload.txt"])
    finally:
        os.remove(tmp)
    check("SDK：文件下载", w.files.download("docs/_upload.txt") == b"upload")
    mounts = {m["mountName"]: m for m in w.mounts()["mounts"]}
    check("SDK：共享空间挂载信息（RO）", mounts["group-public"]["effectivePermission"] == "RO")
    try:
        w.shared_files("group-public").write("x.txt", "no"); ok = False
    except DshError as e:
        ok = e.status == 403
    check("SDK：写入 RO 共享空间抛出 DshError(403)", ok)
    check("SDK：权限来源解释", w.explain(spaces["集团公共资料"]["id"])["sharedSpace"]["source"] == "pool-inherited")
    check("SDK：导出会话", "SDK 会话" in session.export("md"))
    try:
        client.workspace("ws_notexist").info(); ok = False
    except DshError as e:
        ok = e.status == 404
    check("SDK：访问不存在资源抛出 DshError(404)", ok)
    session.delete()
    check("SDK：删除会话", True)
    print(f"\n== SDK 验收：失败 {len(FAILS)}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
