"""Use installed model metadata without guessing a gateway's actual capacity."""
import json
from pathlib import Path
import subprocess

CATALOG = '/usr/local/lib/node_modules/@deepseek-ai/dsh/node_modules/@earendil-works/pi-ai/dist/models.generated.js'
QUERY = '''
import fs from 'node:fs';
import { MODELS } from 'file://''' + CATALOG + '''';
const query = JSON.parse(fs.readFileSync(0, 'utf8'));
const matches = Object.entries(MODELS).flatMap(([provider, models]) =>
  Object.values(models).filter(m => m.id === query.model).map(m => ({provider, contextWindow:m.contextWindow, maxTokens:m.maxTokens})));
const preferred = matches.find(m => m.provider === query.provider);
const signatures = new Set(matches.map(m => `${m.contextWindow}:${m.maxTokens}`));
const resolved = preferred ?? (signatures.size === 1 ? matches[0] : null);
console.log(JSON.stringify(resolved ?? {}));
'''


def catalog_capacity(model, provider=None):
    try:
        result = subprocess.run(['node', '--input-type=module', '-e', QUERY],
                                input=json.dumps({'model': model, 'provider': provider}),
                                text=True, capture_output=True, timeout=10, check=True)
        return json.loads(result.stdout)
    except (OSError, subprocess.SubprocessError, ValueError):
        return {}


def apply_capacity(patch, capacity):
    """An explicit context overrides the catalog; unknown models keep DSH defaults."""
    for row in patch:
        profile = row.get('config', {}).get('providers', {}).get('isolated-model')
        if not profile:
            continue
        model = profile['models'][0]
        for field in ('contextWindow', 'maxTokens'):
            value = capacity.get(field)
            if isinstance(value, int) and not isinstance(value, bool) and value > 0:
                explicit = model.get(field)
                model[field] = min(explicit, value) if isinstance(explicit, int) and explicit > 0 else value
        # Keep output capacity consistent when the administrator narrows context.
        context = model.get('contextWindow')
        if isinstance(context, int) and context > 0:
            model['maxTokens'] = min(model.get('maxTokens', context), context)
        return model
    return {}


def prepare(root, config):
    """Shadow the read-only generated patch with a root-owned capacity-resolved file."""
    target = Path(root) / 'workspace/.dsh/agent.json'
    patch = json.loads(target.read_text())
    profile = apply_capacity(patch, catalog_capacity(config['model'], config.get('catalogProvider')))
    resolved = Path(root) / '.model-agent.json'
    resolved.write_text(json.dumps(patch), encoding='utf-8')
    resolved.chmod(0o444)
    subprocess.run(['mount', '--bind', str(resolved), str(target)], check=True)
    subprocess.run(['mount', '-o', 'remount,bind,ro,nosuid,nodev', str(target)], check=True)
    config['maxOutputTokens'] = profile.get('maxTokens', 0)
