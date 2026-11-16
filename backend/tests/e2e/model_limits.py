"""Exercise the real HTTP broker with fixture transport; never calls a provider."""
import http.client
import http.server
import json
from pathlib import Path
import socket
import subprocess
import sys
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'deploy/sandbox'))
import model_proxy
from model_capacity import apply_capacity


class Response:
    status = 200
    def getheader(self, *_): return 'application/json'
    def read1(self, _):
        if getattr(self, 'done', False): return b''
        self.done = True
        return b'{"ok":true}'


class HTTPS:
    requests = []
    def __init__(self, *_, **__): pass
    def request(self, method, path, body, headers): self.requests.append(json.loads(body))
    def getresponse(self): return Response()
    def close(self): pass


def broker(budget=0, max_output=0):
    server_class = http.server.ThreadingHTTPServer
    def server(_, handler): return server_class(('127.0.0.1', 0), handler)
    with patch.object(socket, 'getaddrinfo', return_value=[(socket.AF_INET, socket.SOCK_STREAM, 6, '', ('93.184.216.34', 443))]), \
         patch.object(subprocess, 'run'), patch.object(http.client, 'HTTPSConnection', HTTPS), \
         patch.object(http.server, 'ThreadingHTTPServer', side_effect=server):
        return model_proxy.start({'baseUrl':'https://fixture.example/v1', 'apiKey':'dummy-fixture', 'model':'fixture', 'maxBudget':budget, 'maxOutputTokens':max_output})


def request(server, body):
    connection = http.client.HTTPConnection('127.0.0.1', server.server_port, timeout=5)
    connection.request('POST', '/v1/chat/completions', json.dumps(body), {'Content-Type':'application/json'})
    response = connection.getresponse()
    status, kind, result = response.status, response.getheader('Content-Type'), json.loads(response.read())
    connection.close()
    return status, kind, result


def close(server):
    server.shutdown()
    server.server_close()


def main():
    server = broker()
    try:
        # Exceeds the old 128k cumulative budget and old 512k body cap.
        for _ in range(3):
            assert request(server, {'model':'fixture','messages':[{'role':'user','content':'x'*600000}], 'max_tokens':8192})[0] == 200
            assert HTTPS.requests[-1]['max_tokens'] == 8192
        assert request(server, {'model':'fixture'})[0] == 200
        assert 'max_tokens' not in HTTPS.requests[-1], 'zero mode added an output cap'
        assert request(server, {'model':'foreign'})[0] == 403, 'model isolation regressed'
        assert request(server, {'model':'fixture','max_tokens':True})[0] == 400
    finally: close(server)
    server = broker(5000)
    try:
        body = {'model':'fixture','max_tokens':4096}
        assert request(server, body)[0] == 200
        status, kind, data = request(server, body)
        assert status == 429 and 'application/json' in kind
        assert data['error']['code'] == 'execution_model_budget_exceeded'
        assert data['error']['remainingBudget'] < data['error']['requiredBudget']
        assert '<html' not in json.dumps(data)
    finally: close(server)
    server = broker(max_output=6000)
    try:
        assert request(server, {'model':'fixture','max_tokens':8192})[0] == 200
        assert HTTPS.requests[-1]['max_tokens'] == 6000
        for _ in range(99): assert request(server, {'model':'fixture','max_tokens':1})[0] == 200
        assert request(server, {'model':'fixture','max_tokens':1})[2]['error']['code'] == 'execution_model_call_limit'
    finally: close(server)
    def model_patch(context=None):
        model = {'id':'fixture'}
        if context is not None: model['contextWindow'] = context
        return [{'config':{'providers':{'isolated-model':{'models':[model]}}}}]
    explicit = apply_capacity(model_patch(65536), {'contextWindow':262144,'maxTokens':32768})
    assert explicit['contextWindow'] == 65536 and explicit['maxTokens'] == 32768
    native = apply_capacity(model_patch(), {'contextWindow':262144,'maxTokens':32768})
    assert native['contextWindow'] == 262144
    assert apply_capacity(model_patch(1000000), {'contextWindow':262144})['contextWindow'] == 262144
    assert apply_capacity(model_patch(1024), {'contextWindow':262144,'maxTokens':32768})['maxTokens'] == 1024
    unknown = apply_capacity(model_patch(), {})
    assert 'contextWindow' not in unknown and 'maxTokens' not in unknown
    print('PASS: zero/native mode; finite budget JSON errors; >4096 output; native output bounds; separate call cap; explicit/native/unknown context; model isolation')


if __name__ == '__main__': main()
