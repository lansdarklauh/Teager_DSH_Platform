"""Real DSH CLI + production isolated supervisor/model broker contract.

The trusted test runner mocks HTTPS transport only. No model provider is called,
no real API key is loaded, and production code has no test bypass.
"""
import json
import os
import subprocess
import tempfile
from pathlib import Path

RUNNER = '''import sys, json, http.client, socket
sys.path.insert(0, '/opt/sandbox')
import model_proxy
class Response:
 status=200
 def __init__(self, request):
  self.parts=[('data: '+json.dumps({'id':'fixture','object':'chat.completion.chunk','created':1,'model':'fixture-model','choices':[{'index':0,'delta':{'role':'assistant','content':'DSH_CONTRACT_VERIFIED'},'finish_reason':None}]})+'\\n\\n').encode(), ('data: '+json.dumps({'id':'fixture','object':'chat.completion.chunk','created':1,'model':'fixture-model','choices':[{'index':0,'delta':{},'finish_reason':'stop'}],'usage':{'prompt_tokens':10,'completion_tokens':5,'total_tokens':15}})+'\\n\\n').encode(), b'data: [DONE]\\n\\n']
  tools=request.get('tools',[])
  bash=next((tool['function']['name'] for tool in tools if 'bash' in tool.get('function',{}).get('name','')),None)
  if bash and not any(message.get('role')=='tool' for message in request.get('messages',[])):
   delta={'role':'assistant','tool_calls':[{'index':0,'id':'fixture-call','type':'function','function':{'name':bash,'arguments':json.dumps({'command':"printf 'AGENT_TOOL_VERIFIED' > agent-proof.txt; id; test ! -r /proc/1/environ", 'description':'Verify personal filesystem isolation'})}}]}
   self.parts=[('data: '+json.dumps({'id':'fixture','object':'chat.completion.chunk','created':1,'model':'fixture-model','choices':[{'index':0,'delta':delta,'finish_reason':None}]})+'\\n\\n').encode(), ('data: '+json.dumps({'id':'fixture','object':'chat.completion.chunk','created':1,'model':'fixture-model','choices':[{'index':0,'delta':{},'finish_reason':'tool_calls'}]})+'\\n\\n').encode(), b'data: [DONE]\\n\\n']
 def getheader(self, name, default=None): return 'text/event-stream'
 def read1(self, size): return self.parts.pop(0) if self.parts else b''
class HTTPS:
 def __init__(self,*args,**kwargs): pass
 def request(self,method,path,body,headers):
  request=json.loads(body); assert request['model']=='fixture-model'; assert headers['Authorization']=='Bearer fixture-upstream-secret'
  assert request.get('max_tokens',8192)<=8192
  self.body=request; print('BROKER_CONTRACT_VERIFIED',flush=True)
 def getresponse(self): return Response(self.body)
 def close(self): pass
http.client.HTTPSConnection=HTTPS
original=socket.getaddrinfo
socket.getaddrinfo=lambda host,*args,**kwargs: [(socket.AF_INET,socket.SOCK_STREAM,6,'',('93.184.216.34',443))] if host=='fixture.model.example' else original(host,*args,**kwargs)
import entrypoint
raise SystemExit(entrypoint.main())
'''

def main():
    with tempfile.TemporaryDirectory(prefix='dsh_agent_contract_') as directory:
        root = Path(directory)
        for name in ('files', 'home', 'locks', 'control/managed'):
            (root / name).mkdir(parents=True)
        patch = [
            {'id': 'agent-default-model', 'name': '@deepseek-ai/dsh-agent-default-model', 'config': {'provider': 'isolated-model', 'model': 'fixture-model'}},
            {'id': 'llm-pi-ai', 'name': '@deepseek-ai/dsh-llm-pi-ai', 'config': {'providers': {'isolated-model': {'displayName': 'Fixture', 'apiKeyEnv': 'DSH_ISOLATED_MODEL_KEY', 'api': 'openai-completions', 'baseURL': 'http://127.0.0.1:8765/v1', 'models': [{'id': 'fixture-model', 'name': 'Fixture', 'contextWindow':65536, 'maxTokens':8192}]}}}}
        ]
        (root / 'control/managed/agent.json').write_text(json.dumps(patch), encoding='utf-8')
        (root / 'control/fixture_runner.py').write_text(RUNNER, encoding='utf-8')
        (root / 'control/prompt.txt').write_text('Reply with DSH_CONTRACT_VERIFIED.', encoding='utf-8')
        job = {'executionId': 'contract', 'workspaceId': 'contract', 'command': ['dsh', 'headless', '--patch', '/workspace/.dsh/agent.json', '--json', '-'], 'terminal': False, 'storageBytes': 16*1024*1024, 'maxFiles': 2000, 'mounts': [], 'modelProxy': {'baseUrl': 'https://fixture.model.example/v1', 'apiKey': 'fixture-upstream-secret', 'model': 'fixture-model', 'maxBudget': 0}, 'preview': None, 'networkHosts': []}
        (root / 'control/job.json').write_text(json.dumps(job), encoding='utf-8')
        args = ['docker', 'run', '--rm', '--read-only', '--cap-drop', 'ALL', '--device', '/dev/fuse', '--security-opt', 'apparmor=unconfined', '--security-opt', 'no-new-privileges', '--tmpfs', '/sandbox:rw,size=16m,mode=755']
        for capability in ('SYS_ADMIN', 'SETUID', 'SETGID', 'SYS_CHROOT', 'NET_ADMIN'):
            args += ['--cap-add', capability]
        for source, target in [('files','/input/files'),('home','/input/home'),('locks','/locks'),('control','/control')]:
            args += ['--mount',f'type=bind,source={root/source},target={target}' + (',readonly' if source=='control' else '')]
        args += ['--entrypoint','python3',os.environ.get('DSH_SANDBOX_TEST_IMAGE', 'teager-dsh-sandbox:usage-only'),'/control/fixture_runner.py']
        container = 'dsh-usage-contract-' + root.name.replace('_', '-').lower()
        args[2:2] = ['--name', container]
        try:
            result = subprocess.run(args,capture_output=True,timeout=90)
        finally:
            subprocess.run(['docker', 'rm', '-f', container], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        output = result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace')
        Path('.cache/usage-validation/agent-contract.log').write_text(output,encoding='utf-8')
        print(output[-5000:])
        assert result.returncode == 0 and 'BROKER_CONTRACT_VERIFIED' in output and 'DSH_CONTRACT_VERIFIED' in output, result.returncode
        assert 'fixture-upstream-secret' not in output, 'upstream secret leaked'
        assert (root / 'files/agent-proof.txt').read_text(encoding='utf-8') == 'AGENT_TOOL_VERIFIED', 'DSH did not execute its tool in the personal filesystem'
        print('PASS: real DSH CLI consumes production patch and isolated broker without exposing its key')

if __name__ == '__main__':
    main()
