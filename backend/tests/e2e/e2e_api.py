#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
DSH 平台端到端接口验收脚本（仅用标准库）。

前置：平台以演示数据模式运行（Dsh__Seed__DemoData=true，Provider 默认为 Mock），管理员 admin / admin123，
演示用户 a~e 密码 Demo@1234。用法：
    python e2e_api.py [http://localhost:5600]
全部断言通过退出码为 0，否则为 1。
"""
import http.cookiejar
import io
import json
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5600").rstrip("/")
API = BASE + "/api/v1"
FAILS: list[str] = []
PASSED = 0


def check(name: str, cond: bool, extra: str = ""):
    """记录一条断言结果。"""
    global PASSED
    if cond:
        PASSED += 1
        print(f"  [PASS] {name}")
    else:
        FAILS.append(name)
        print(f"  [FAIL] {name} {extra}")


class Client:
    """带 Cookie 会话或 Bearer Token 的简易 HTTP 客户端。"""

    def __init__(self, token: str | None = None):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.token = token

    def call(self, method, path, body=None, headers=None, raw=False, form=None, csrf=True):
        url = path if path.startswith("http") else API + path
        h = dict(headers or {})
        data = None
        if form is not None:  # multipart 上传
            boundary = "----e2e" + uuid.uuid4().hex
            buf = io.BytesIO()
            for name, (filename, content) in form.items():
                buf.write(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{filename}\"\r\nContent-Type: application/octet-stream\r\n\r\n".encode())
                buf.write(content if isinstance(content, bytes) else content.encode())
                buf.write(b"\r\n")
            buf.write(f"--{boundary}--\r\n".encode())
            data = buf.getvalue()
            h["Content-Type"] = "multipart/form-data; boundary=" + boundary
        elif body is not None:
            data = json.dumps(body).encode()
            h["Content-Type"] = "application/json"
        if self.token:
            h["Authorization"] = "Bearer " + self.token
        elif csrf:
            h["X-Requested-With"] = "dsh-web"
        req = urllib.request.Request(url, data=data, method=method, headers=h)
        try:
            with self.opener.open(req, timeout=120) as r:
                payload = r.read()
                return r.status, (payload if raw else (json.loads(payload) if payload else None))
        except urllib.error.HTTPError as e:
            payload = e.read()
            try:
                return e.code, json.loads(payload) if payload else None
            except Exception:
                return e.code, payload.decode(errors="replace")

    def get(self, p, **k): return self.call("GET", p, **k)
    def post(self, p, b=None, **k): return self.call("POST", p, b if b is not None else {}, **k)
    def put(self, p, b=None, **k): return self.call("PUT", p, b, **k)
    def patch(self, p, b=None, **k): return self.call("PATCH", p, b, **k)
    def delete(self, p, **k): return self.call("DELETE", p, **k)


def login(user, pwd):
    c = Client()
    s, r = c.post("/auth/login", {"username": user, "password": pwd})
    assert s == 200, f"登录 {user} 失败：{s} {r}"
    return c


def names(items): return sorted(i["name"] for i in items)


def stream_events(c: Client, session_id: str, after=0, timeout=60):
    """读取 SSE 事件流直到关闭，返回事件列表。"""
    req = urllib.request.Request(f"{API}/sessions/{session_id}/events?after={after}", headers={"X-Requested-With": "dsh-web"})
    out = []
    with c.opener.open(req, timeout=timeout) as r:
        assert "text/event-stream" in r.headers.get("Content-Type", "")
        data = []
        for line in r:
            line = line.decode().rstrip("\n")
            if line.startswith("data: "):
                data.append(line[6:])
            elif line == "" and data:
                out.append(json.loads("\n".join(data))); data = []
    return out


def main():
    print(f"== 目标：{BASE}")
    print("-- 认证与网关")
    s, _ = Client().get("/pools")
    check("未登录访问返回 401", s == 401)
    s, _ = Client().post("/auth/login", {"username": "a", "password": "wrong"})
    check("错误密码返回 401", s == 401)
    a = login("a", "Demo@1234"); b = login("b", "Demo@1234"); cc = login("c", "Demo@1234"); e = login("e", "Demo@1234")
    admin = login("admin", "admin123")
    s, r = a.post("/pools", {"name": "x"}, csrf=False)
    check("带 Cookie 的写请求缺少 CSRF 头被拒绝", s == 403, str(r))
    s, me = a.get("/auth/me")
    check("用户 A 的有效 Pool = 编辑部 + 集团", s == 200 and sorted(p["name"] for p in me["effectivePools"]) == ["编辑部", "集团"], str(me))

    print("-- Pool 树与继承可见性")
    s, tree = a.get("/pools")
    check("A 看到集团与编辑部，看不到市场部", names(tree) == ["编辑部", "集团"], str(names(tree)))
    s, tree_e = e.get("/pools")
    check("E（直属集团）只看到集团", names(tree_e) == ["集团"], str(names(tree_e)))
    pool = {p["name"]: p for p in admin.get("/pools")[1]}
    edit_id, group_id, market_id = pool["编辑部"]["id"], pool["集团"]["id"], pool["市场部"]["id"]
    s, d = a.get(f"/pools/{edit_id}")
    check("Pool 详情含祖先链与我的权限", s == 200 and d["ancestors"][0]["name"] == "集团" and d["my"]["canManage"] is True, str(d)[:200])
    s, _ = cc.get(f"/pools/{edit_id}")
    check("兄弟 Pool 成员访问编辑部返回 404", s == 404)
    s, m = admin.get(f"/pools/{group_id}/members")
    check("集团成员含继承成员（来自子 Pool）", s == 200 and {x["username"] for x in m["inherited"]} >= {"a", "b", "c", "d"}, str(m)[:300])

    print("-- 共享空间与权限来源解释")
    s, spaces_a = a.get("/shared-spaces")
    check("A 可见集团公共 + 编辑内部，不可见市场内部", names(spaces_a) == ["编辑内部资料", "集团公共资料"], str(names(spaces_a)))
    s, spaces_e = e.get("/shared-spaces")
    check("E 只可见集团公共资料", names(spaces_e) == ["集团公共资料"], str(names(spaces_e)))
    sp = {x["name"]: x for x in spaces_a}
    c1, a1 = sp["集团公共资料"]["id"], sp["编辑内部资料"]["id"]
    check("集团公共资料对 A 为 RO 且来自继承", sp["集团公共资料"]["permission"] == "RO" and sp["集团公共资料"]["source"] == "pool-inherited")
    s, ex = a.get(f"/shared-spaces/{c1}/permissions/explain")
    check("权限解释包含继承路径 集团→编辑部", s == 200 and ex["path"][0] == "集团", str(ex))
    s, _ = e.get(f"/shared-spaces/{a1}")
    check("E 访问编辑内部资料返回 404", s == 404)

    print("-- Workspace / 挂载 / 文件")
    s, ws = a.post("/workspaces", {"poolId": edit_id, "name": "酒店研究-" + uuid.uuid4().hex[:4]})
    check("创建 Workspace", s == 200 and ws.get("id"), str(ws))
    wid = ws["id"]
    s, _ = a.post(f"/workspaces/{wid}/mounts", {"sharedSpaceId": c1, "mountName": "group"})
    check("挂载集团公共资料", s == 204)
    s, _ = a.post(f"/workspaces/{wid}/mounts", {"sharedSpaceId": c1, "mountName": "dup"})
    check("重复挂载被拒绝", s == 409)
    s, _ = a.post(f"/workspaces/{wid}/mounts", {"sharedSpaceId": a1, "mountName": "editorial", "permissionOverride": "RO"})
    check("挂载编辑内部资料并降为 RO", s == 204)
    s, _ = a.post(f"/workspaces/{wid}/mounts", {"sharedSpaceId": pool["市场部"]["id"] and sp.get("市场内部资料", {}).get("id", "none")})
    check("挂载未授权/不可见的共享空间被拒绝", s in (400, 403, 404))
    s, mt = a.get(f"/workspaces/{wid}/mounts")
    mm = {m_["mountName"]: m_ for m_ in mt["mounts"]}
    check("挂载有效权限：group=RO，editorial=RO(覆盖降级)", mm["group"]["effectivePermission"] == "RO" and mm["editorial"]["effectivePermission"] == "RO", str(mm)[:300])
    s, ex = a.get(f"/workspaces/{wid}/permissions/explain?sharedSpaceId={c1}")
    check("Workspace 权限解释含 Workspace 路径", s == 200 and ex["sharedSpace"]["path"][-1].startswith("Workspace:"), str(ex)[:300])
    s, _ = a.post(f"/workspaces/{wid}/files/mkdir", {"path": "docs"})
    check("创建目录", s == 204)
    s, _ = a.post(f"/workspaces/{wid}/files/upload?path=docs", form={"files": ("hello.txt", "你好 DSH")})
    check("上传文件", s == 200)
    s, ls = a.get(f"/workspaces/{wid}/files?path=docs")
    check("列出目录", s == 200 and any(x["name"] == "hello.txt" for x in ls["entries"]), str(ls)[:200])
    s, rd = a.get(f"/workspaces/{wid}/files/content?path=docs/hello.txt")
    check("读取文件内容", s == 200 and rd["content"] == "你好 DSH")
    s, _ = a.put(f"/workspaces/{wid}/files/content", {"path": "docs/notes.md", "content": "# 笔记"})
    check("写入文本文件", s == 204)
    s, _ = a.post(f"/workspaces/{wid}/files/move", {"from": "docs/notes.md", "to": "docs/notes2.md"})
    check("重命名文件", s == 204)
    s, raw = a.get(f"/workspaces/{wid}/files/download?path=docs/hello.txt", raw=True)
    check("下载文件", s == 200 and "你好 DSH".encode() in raw)
    s, r = a.get(f"/workspaces/{wid}/files/content?path=../../etc/passwd")
    check("路径穿越被拒绝", s == 400, str(r))
    s, r = a.put(f"/workspaces/{wid}/files/content?area=shared/group", {"path": "x.txt", "content": "no"})
    check("向只读共享空间(RO)写入被拒绝 403", s == 403, str(r))
    s, r = a.put(f"/workspaces/{wid}/files/content?area=shared/editorial", {"path": "x.txt", "content": "no"})
    check("挂载覆盖降为 RO 后写入被拒绝 403", s == 403, str(r))
    s, _ = a.patch(f"/workspaces/{wid}/mounts/{mm['editorial']['id']}", {"clearOverride": True})
    s, _ = a.put(f"/workspaces/{wid}/files/content?area=shared/editorial", {"path": "from-e2e.txt", "content": "编辑部资料"})
    check("清除覆盖后可写入 RW 共享空间", s == 204)
    s, ls = a.get(f"/shared-spaces/{a1}/files")
    check("共享空间文件直接可见", s == 200 and any(x["name"] == "from-e2e.txt" for x in ls["entries"]))
    s, _ = e.get(f"/workspaces/{wid}/files")
    check("非成员访问 Workspace 文件返回 404", s == 404)

    print("-- 会话与 Agent（Mock Runtime）")
    s, ses = a.post("/sessions", {"workspaceId": wid})
    check("创建会话", s == 200, str(ses))
    sid = ses["id"]
    s, r = a.post(f"/sessions/{sid}/messages?wait=true", {"content": "你好，请介绍一下你自己"})
    check("同步发送消息返回最终文本", s == 200 and r["status"] == "Completed" and "模拟回复" in (r.get("text") or ""), str(r))
    s, r = a.post(f"/sessions/{sid}/messages", {"content": "/write files/out.txt 由Agent写入"})
    check("异步发送返回 202 与 runId", s == 202 and r.get("runId"), str(r))
    evs = stream_events(a, sid, after=0)
    types = [x["type"] for x in evs]
    check("SSE 回放并跟随到 run_end", types[-1:] == ["run_end"] and "tool_call" in types and "tool_result" in types, str(types)[-200:])
    s, msgs = a.get(f"/sessions/{sid}/messages")
    check("消息落库（user/assistant 各 2 条）", s == 200 and [m_["role"] for m_ in msgs] == ["user", "assistant", "user", "assistant"], str([m_["role"] for m_ in msgs]))
    s, rd = a.get(f"/workspaces/{wid}/files/content?path=out.txt")
    check("Agent 写入的文件出现在私有文件目录", s == 200 and rd["content"] == "由Agent写入", str(rd))
    s, runs = a.get(f"/sessions/{sid}/runs")
    check("运行记录含 token 用量", s == 200 and runs[0]["inputTokens"] > 0)
    s, hist = a.get(f"/sessions/{sid}/events/history?runId={runs[0]['id']}")
    check("按运行回放历史事件", s == 200 and any(x["type"] == "run_start" for x in hist))
    s, found = a.get("/sessions/search?q=" + urllib.request.quote("介绍"))
    check("会话内容搜索命中", s == 200 and any(x["id"] == sid for x in found), str(found))
    s, r = a.patch(f"/sessions/{sid}", {"title": "酒店研究首轮"})
    check("会话重命名", s == 200 and r["title"] == "酒店研究首轮")
    s, r = a.get(f"/sessions/{sid}/export?format=md", raw=True)
    check("会话导出 Markdown", s == 200 and "酒店研究首轮".encode() in r)
    s, dup = a.post(f"/sessions/{sid}/duplicate")
    check("会话复制", s == 200 and dup["id"] != sid)
    s, r = a.post(f"/sessions/{dup['id']}/messages", {"content": "/sleep 20"})
    time.sleep(0.8)
    s2, r2 = a.post(f"/sessions/{dup['id']}/cancel")
    check("取消运行中的任务", s == 202 and s2 == 200 and r2["cancelled"] is True, f"{s} {s2} {r2}")
    s, _ = e.get(f"/sessions/{sid}")
    check("非成员访问会话返回 404", s == 404)

    print("-- Runtime / 策略 / 配置")
    s, rt = a.get(f"/pools/{edit_id}/runtime")
    check("Runtime 状态可查（不含内部端点）", s == 200 and "internalEndpoint" not in rt and rt["status"] in ("IDLE", "RUNNING"), str(rt))
    s, rt = a.post(f"/pools/{edit_id}/runtime/stop")
    check("停止 Runtime", s == 200 and rt["status"] == "STOPPED", str(rt))
    s, rt = a.post(f"/pools/{edit_id}/runtime/restart")
    check("重启 Runtime", s == 200 and rt["status"] in ("IDLE", "RUNNING"), str(rt))
    s, h = a.get(f"/pools/{edit_id}/runtime/health")
    check("Runtime 健康检查", s == 200 and h["healthy"] is True)
    s, r = admin.put(f"/pools/{group_id}/policy", {"maxWorkspaces": 50, "allowNetwork": False})
    check("集团策略：禁止网络", s == 200, str(r))
    s, pol = admin.get(f"/pools/{edit_id}/policy")
    check("子 Pool 有效策略继承 allowNetwork 限制", s == 200 and (pol["effective"]["allowNetwork"] is False if s == 200 else False), str(pol)[:300])
    s, r = a.put(f"/pools/{edit_id}/policy", {"allowNetwork": True})
    check("子 Pool 不能放宽上级策略", s == 400, str(r))
    s, r = admin.put("/configs", {"kind": "Model", "scope": "Platform", "key": "deepseek-flash", "displayName": "DeepSeek Flash", "params": {"provider": "deepseek-official", "isDefault": True}})
    check("平台层配置模型", s == 200, str(r))
    s, r = a.put("/configs", {"kind": "Model", "scope": "Pool", "scopeId": edit_id, "key": "sneaky", "params": {"provider": "x"}})
    check("Pool 管理员不能自行新增模型（需授权）", s == 403, str(r))
    s, r = a.put("/configs", {"kind": "Model", "scope": "Pool", "scopeId": edit_id, "key": "deepseek-flash", "enabled": False})
    check("子级可停用上级模型（收紧）", s == 200, str(r))
    s, eff = a.get(f"/workspaces/{wid}/effective-config")
    m_ = [x for x in eff if x["key"] == "deepseek-flash"][0]
    check("有效配置：模型被编辑部停用并标明来源", s == 200 and m_["enabled"] is False and m_["disabledAt"] == "编辑部", str(m_))
    s, r = a.put("/configs", {"kind": "Skill", "scope": "Pool", "scopeId": edit_id, "key": "weekly-report", "displayName": "周报", "content": "# 周报写作规范\n请按模板输出。"})
    check("Pool 管理员可新增 Skill", s == 200, str(r))
    s, r = a.put("/secrets", {"scope": "Pool", "scopeId": edit_id, "name": "MY_API_KEY", "value": "sk-secret-value"})
    check("设置 Pool 密钥", s == 204, str(r))
    s, lst = a.get(f"/secrets?scope=Pool&scopeId={edit_id}")
    check("密钥列表不返回明文", s == 200 and "sk-secret-value" not in json.dumps(lst) and lst[0]["name"] == "MY_API_KEY")
    s, _ = e.get(f"/secrets?scope=Pool&scopeId={edit_id}")
    check("非成员读取密钥列表被拒绝", s in (403, 404))

    print("-- Token / SDK 身份")
    s, tk = a.post("/tokens", {"name": "e2e", "scopes": ["workspace:read", "session:read"], "allowedWorkspaces": [wid]})
    check("创建个人 Token（明文仅返回一次）", s == 200 and tk["token"].startswith("dshp_"), str(tk))
    t = Client(token=tk["token"])
    s, lst = t.get("/workspaces")
    check("Token 可读取 Workspace 列表且被白名单限定", s == 200 and [w["id"] for w in lst] == [wid], str(lst)[:200])
    s, r = t.post("/sessions", {"workspaceId": wid})
    check("Token 缺少 session:create Scope 返回 403", s == 403, str(r))
    s, r = t.post("/tokens", {"name": "x", "scopes": ["pool:read"]})
    check("Token 不能创建 Token", s == 403, str(r))
    s, lst = a.get("/tokens")
    check("Token 列表不含明文", s == 200 and "token" not in json.dumps(lst).replace("tokenPrefix", ""), str(lst)[:200])
    s, _ = a.delete(f"/tokens/{tk['id']}")
    s, _ = t.get("/workspaces")
    check("吊销后 Token 失效", s == 401)
    s, tk2 = a.post("/tokens", {"name": "sdk", "scopes": ["workspace:read", "session:create", "session:execute", "session:read"], "allowedWorkspaces": [wid]})
    sdk = Client(token=tk2["token"])
    s, ses2 = sdk.post("/sessions", {"workspaceId": wid})
    s, r = sdk.post(f"/sessions/{ses2['id']}/messages?wait=true", {"content": "SDK 调用测试"})
    check("Bearer Token 走同一套会话接口并同步返回结果", s == 200 and r["status"] == "Completed", str(r))

    print("-- 审计 / 用量 / 系统")
    s, au = admin.get("/audit?pageSize=200")
    acts = {x["action"] for x in au["items"]}
    need = {"auth.login", "workspace.create", "session.run", "sharedspace.write", "workspace.mount.add", "token.create", "config.upsert", "secret.upsert", "runtime.stop"}
    check("审计覆盖登录/Workspace/Session/共享空间/权限变更", s == 200 and need <= acts, str(need - acts))
    s, au = a.get(f"/audit?poolId={edit_id}&action=sharedspace")
    check("Pool 管理员可按 Pool 查询共享空间审计", s == 200 and au["total"] >= 1)
    s, au = e.get(f"/audit?poolId={edit_id}")
    check("无关用户看不到其他 Pool 的审计", s == 200 and au["total"] == 0, str(au["total"]))
    s, us = a.get(f"/usage/summary?workspaceId={wid}&days=7")
    check("Workspace 用量统计", s == 200 and us["totals"]["runs"] >= 3 and us["totals"]["inputTokens"] > 0, str(us["totals"]))
    s, us = admin.get("/usage/summary?days=7")
    check("平台用量按 Pool 汇总", s == 200 and len(us["byPool"]) >= 1)
    s, si = admin.get("/system/info")
    check("系统信息含数据/日志/缓存目录", s == 200 and {"data", "logs", "cache"} <= set(si["directories"].keys()), str(si)[:200])
    s, _ = a.get("/system/info")
    check("普通用户不能访问系统信息", s == 403)
    s, rts = admin.get("/runtimes")
    check("管理员查看所有 Runtime", s == 200 and len(rts) >= 3)

    print("-- Pool 移动预览 / 快照 / 克隆 / 删除")
    s, np = admin.post("/pools", {"name": "AI 实验室", "description": "顶级"})
    s, prev = admin.get(f"/pools/{edit_id}/move-preview?newParentId={np['id']}")
    check("移动预览：失去集团，获得 AI 实验室", s == 200 and prev["lostAncestors"] == ["集团"] and prev["gainedAncestors"] == ["AI 实验室"] and prev["affectedWorkspaces"] >= 1, str(prev))
    s, snap = a.post(f"/workspaces/{wid}/snapshots", {"name": "e2e-snap"})
    check("创建快照", s == 200 and snap["sizeBytes"] > 0, str(snap))
    s, cl = a.post(f"/workspaces/{wid}/clone", {"name": "克隆-" + uuid.uuid4().hex[:4]})
    check("克隆 Workspace", s == 200, str(cl))
    s, rd = a.get(f"/workspaces/{cl['id']}/files/content?path=docs/hello.txt")
    check("克隆包含原文件", s == 200 and rd["content"] == "你好 DSH")
    s, _ = a.delete(f"/workspaces/{cl['id']}?mode=archive")
    check("归档 Workspace（默认 30 天保留）", s == 204)
    s, _ = a.post(f"/workspaces/{cl['id']}/restore")
    check("恢复归档", s == 204)
    s, _ = a.delete(f"/workspaces/{cl['id']}?mode=permanent")
    check("永久删除 Workspace", s == 204)
    s, r = admin.delete(f"/pools/{np['id']}")
    check("删除无子项 Pool 成功（归档）", s == 204, str(r))

    print(f"\n== 结果：通过 {PASSED}，失败 {len(FAILS)}")
    for f in FAILS: print("   ✗", f)
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
