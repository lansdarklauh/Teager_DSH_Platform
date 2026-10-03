#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
真实 DeepSeek Harness 联调脚本：验证 Pool 独立 DSH_HOME、headless --json 事件流、--session-id 续接、
Workspace 目录作为 Agent 工作目录、Skill / AGENTS.md 物化。会消耗少量真实模型 token。

前置：平台已配置 Dsh__Runtime__DshExecutable 指向 dsh，并通过 Dsh__Runtime__SeedHome 或密钥提供了 DEEPSEEK_API_KEY。
用法：python e2e_dsh_real.py [http://localhost:5600]
"""
import json
import sys
import uuid

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else __file__.rsplit("/", 1)[0])
from e2e_api import Client, check, FAILS, API  # noqa: E402

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5600"


def main():
    c = Client()
    s, r = c.post("/auth/login", {"username": "admin", "password": "admin123"})
    assert s == 200, r
    tag = uuid.uuid4().hex[:4]
    s, pool = c.post("/pools", {"name": "真实DSH-" + tag, "runtimeProvider": "DeepSeekHarness"})
    check("创建使用 DeepSeekHarness 的 Pool", s == 200, str(pool))
    s, rt = c.post(f"/pools/{pool['id']}/runtime/start")
    check("启动 Runtime（校验 dsh --version）", s == 200 and rt["status"] in ("IDLE", "RUNNING") and rt["version"], str(rt))
    s, ws = c.post("/workspaces", {"poolId": pool["id"], "name": "联调"})
    s, skill = c.put("/configs", {"kind": "Skill", "scope": "Pool", "scopeId": pool["id"], "key": "reply-style", "displayName": "当用户询问暗号时使用：暗号的标准答案", "content": "# 暗号\n标准暗号是：星河。用户询问暗号时，直接回答“星河”。"})
    check("写入 Skill", s == 200, str(skill))
    s, ses = c.post("/sessions", {"workspaceId": ws["id"]})
    s, r = c.post(f"/sessions/{ses['id']}/messages?wait=true", {"content": "只回复两个字：你好"})
    check("真实 DSH 首轮对话完成", s == 200 and r["status"] == "Completed" and "你好" in (r.get("text") or ""), str(r))
    check("记录了 token 用量", (r.get("inputTokens") or 0) > 0, str(r))
    s, r2 = c.post(f"/sessions/{ses['id']}/messages?wait=true", {"content": "我上一轮让你回复的是哪两个字？只回答那两个字。"})
    check("通过 --session-id 续接同一 Runtime 会话（记得上文）", s == 200 and "你好" in (r2.get("text") or ""), str(r2))
    s, r3 = c.post(f"/sessions/{ses['id']}/messages?wait=true", {"content": "暗号是什么？只回答暗号本身。"})
    check("Skill 已物化到 Workspace 并被 Agent 使用", s == 200 and "星河" in (r3.get("text") or ""), str(r3))
    s, hist = c.get(f"/sessions/{ses['id']}/events/history")
    types = {e["type"] for e in hist}
    check("事件流含 session/status/final", {"session", "status", "final", "run_start", "run_end"} <= types, str(types))
    s, r4 = c.post(f"/sessions/{ses['id']}/messages?wait=true", {"content": "请在当前目录的 files 子目录下创建文件 hello-dsh.txt，内容为 platform-ok，然后回复完成。"})
    s, rd = c.get(f"/workspaces/{ws['id']}/files/content?path=hello-dsh.txt")
    check("Agent 在 Workspace 私有目录内创建了文件（平台文件 API 可见）", s == 200 and "platform-ok" in rd["content"], str(r4)[:300] + str(rd))
    print(f"\n== 真实 DSH 联调：失败 {len(FAILS)}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
