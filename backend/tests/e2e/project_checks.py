"""Real project-reference message/API/SDK checks in the disposable API fixture."""
import sys
from pathlib import Path
import urllib.parse


def check_projects(alice, bob, aw, bw, checks):
    first = alice.call('GET', f'workspaces/{aw}/projects')[0]
    second = alice.call('POST', f'workspaces/{aw}/projects', {'name': '第二个项目', 'description': '项目说明' * 250})
    foreign = bob.call('POST', f'workspaces/{bw}/projects', {'name': '私有项目'})
    session = alice.call('POST', 'sessions', {'workspaceId': aw})['id']
    body = {'content': '检查关联项目', 'projectIds': [first['id'], second['id']], 'requestId': 'project-reference'}
    result = alice.call('POST', f'sessions/{session}/messages?wait=true', body)
    assert result['status'] == 'Completed' and first['directory'] in result['text'], result
    history = alice.call('GET', f'sessions/{session}/messages')
    assert history[0]['content'] == body['content']
    assert [p['id'] for p in history[0]['projects']] == body['projectIds']
    assert history[0]['projects'][1]['description'] == '项目说明' * 250
    events = alice.call('GET', f'sessions/{session}/events/history?runId={result["runId"]}')
    assert len(next(e for e in events if e['type'] == 'run_start')['projects']) == 2
    checks.append('project references reach Agent context and persist as separate metadata, including long Chinese descriptions')
    retry = alice.call('POST', f'sessions/{session}/messages', body, expected=202)
    assert retry['runId'] == result['runId']
    alice.call('POST', f'sessions/{session}/messages', {**body, 'projectIds': [first['id']]}, expected=409)
    copied = alice.call('POST', f'sessions/{session}/duplicate')['id']
    assert alice.call('GET', f'sessions/{copied}/messages')[0]['projects'] == history[0]['projects']
    assert alice.call('GET', f'sessions/{session}/export?format=json')['messages'][0]['projects'] == history[0]['projects']
    checks.append('project reference retry is idempotent; changing references conflicts; duplication/export preserve references')
    for ids, status in [([foreign['id']], 404), (['prj_missing'], 404), ([first['id'], first['id']], 400), ([f'prj_{n}' for n in range(9)], 400)]:
        alice.call('POST', f'sessions/{session}/messages', {'content': 'invalid', 'projectIds': ids}, expected=status)
    removed = alice.call('POST', f'workspaces/{aw}/projects', {'name': '目录删除'})
    alice.call('DELETE', f'workspaces/{aw}/files?path=' + urllib.parse.quote(removed['directory']), expected=204)
    alice.call('POST', f'sessions/{session}/messages', {'content': 'invalid', 'projectIds': [removed['id']]}, expected=404)
    alice.call('DELETE', f'workspaces/{aw}/projects/{second["id"]}', expected=204)
    alice.call('POST', f'sessions/{session}/messages', {'content': 'invalid', 'projectIds': [second['id']]}, expected=404)
    assert len(alice.call('GET', f'sessions/{session}/messages')) == 2
    checks.append('foreign/missing/removed/directoryless/duplicate/excess project references are rejected without new messages')
    sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'sdk/python'))
    from dsh_platform import Client
    token = alice.call('POST', 'tokens', {'name': 'sdk-project', 'scopes': ['workspace:read', 'session:read', 'session:create', 'session:execute', 'files:read', 'files:write']})['token']
    sdk = Client(alice.base, token).workspace(aw).create_session()
    assert sdk.send('分析引用项目', project_ids=[first['id']], timeout=15).status == 'Completed'
    streamed = list(sdk.stream('继续检查项目', project_ids=[first['id']], timeout=15))
    assert streamed[-1]['type'] == 'run_end'
    assert next(e for e in streamed if e['type'] == 'run_start')['projects'][0]['id'] == first['id']
    assert sdk.messages()[0]['projects'][0]['id'] == first['id']
    sdk.delete()
    checks.append('Python SDK send/stream support project_ids and retain project metadata')
