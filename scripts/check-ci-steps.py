"""usage: ci_steps.py <sha> <workflow-name-substring> [wait_seconds]  -> prints STEPS_OK or the red steps; exit 3 on rate limit"""
import json, sys, time, urllib.request
sha, wf = sys.argv[1], sys.argv[2]; wait = int(sys.argv[3]) if len(sys.argv) > 3 else 0
def get(u):
    r = urllib.request.Request(u, headers={'User-Agent': 'mc-ci-reader'})
    try:
        with urllib.request.urlopen(r, timeout=20) as f: return json.load(f)
    except urllib.error.HTTPError as e:
        if e.code == 403: print('RATE_LIMITED'); sys.exit(3)
        raise
base = 'https://api.github.com/repos/athens96/MightyClaude'
t0 = time.time()
while True:
    runs = [r for r in get(f'{base}/actions/runs?head_sha={sha}&per_page=10').get('workflow_runs', []) if wf in r['name']]
    if runs and runs[0]['status'] == 'completed': break
    if time.time() - t0 > wait: print('NOT_FINISHED', runs[0]['status'] if runs else 'no run'); sys.exit(2)
    time.sleep(60)
red = []
for j in get(f"{base}/actions/runs/{runs[0]['id']}/jobs").get('jobs', []):
    for s in j['steps']:
        if s['conclusion'] not in ('success', 'skipped', None) and 'freeze' not in s['name'].lower(): red.append(f"{j['name']} | {s['name']} | {s['conclusion']}")
        if s['conclusion'] == 'skipped' and 'freeze' not in s['name'].lower() and 'manifest' not in s['name'].lower(): red.append(f"{j['name']} | {s['name']} | skipped")
print('STEPS_OK' if not red else '\n'.join(red))
sys.exit(0 if not red else 1)
