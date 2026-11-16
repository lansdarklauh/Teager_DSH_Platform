"""SDK reconnect/run-filter contract, with no server or external provider."""
import io
import json
from pathlib import Path
import sys
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'sdk/python'))
from dsh_platform import Client, Session


def response(events):
    return io.BytesIO(b''.join(('data: ' + json.dumps(event) + '\r\n\r\n').encode() for event in events))


requests = []
responses = iter([
    response([{'seq': 1, 'runId': 'old', 'type': 'run_end'}, {'seq': 2, 'runId': 'new', 'type': 'text', 'text': 'hello'}]),
    response([{'seq': 2, 'runId': 'new', 'type': 'text', 'text': 'hello'}, {'seq': 3, 'runId': 'new', 'type': 'run_end'}])
])


def open_response(request, **kwargs):
    requests.append(request.full_url)
    return next(responses)


with patch('urllib.request.urlopen', open_response), patch('time.sleep'):
    session = Session(Client('https://fixture.example', 'fixture-token'), 'session')
    rows = list(session.events(run_id='new', timeout=5))
    assert [row['seq'] for row in rows] == [2, 3], rows
    assert requests[-1].endswith('after=2'), requests
print('PASS: SDK reconnect uses latest seq, removes duplicates, filters other runs and stops at its run_end')
