#!/usr/bin/env python3
"""Confirms an effort cut on a few exact requests through the Batch API (#269, from the #265 spec).

Only requests with a saved body (#266) are exact; ones rebuilt from a transcript are skipped. For each kind asked for,
it prints the cost estimate first, and sends only the kinds named with their own --approve flag. Nothing is sent
without one, and --dry-run never sends. The submit step is the injectable `submit` of `main`; the default one needs
ANTHROPIC_API_KEY in the environment and is the only code here that touches the network.

Usage: python3 -I tools/cost-baseline/batch_confirm.py MISSION_OR_SAVES_DIR... --kinds debrief,reply
       [--new-effort medium] [--count 5] [--approve debrief] [--approve reply] [--dry-run]
"""
import argparse
import copy
import json
import os
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python3 -I leaves the script's folder off the path
import baseline  # noqa: E402
import replay  # noqa: E402

BATCH_DISCOUNT = 0.5
DEFAULT_MAX_TOKENS = 32000


def with_effort(messages, effort):
    """The turn's messages with their effort set: the effort messages after the last assistant message are replaced by
    one for `effort`, put right after it, so history keeps the efforts it was sent with."""
    last = max([i for i, m in enumerate(messages) if m.get('role') == 'assistant'], default=-1)
    tail = [m for m in messages[last + 1:] if not (m.get('role') == 'system' and 'output_config' in m)]
    return messages[:last + 1] + [{'role': 'system', 'content': [], 'output_config': {'effort': effort}}] + tail


def batch_request(case, effort):
    """One batch entry: the saved body as sent, but not streamed, at `effort`."""
    body = copy.deepcopy(case['body'])
    body.pop('stream', None)
    body['messages'] = with_effort(body.get('messages') or [], effort)
    cfg = body.get('output_config')
    if isinstance(cfg, dict):
        cfg.pop('effort', None)
    return {'custom_id': case['id'].replace('/', '_'), 'params': body}


def estimate(cases, effort):
    """Dollars for the batch of `cases`: expected (the originals' output tokens) and worst case (max_tokens out).
    Input is priced at the full uncached rate: no cache credit is assumed."""
    total = dict(requests=len(cases), expected=0.0, worst=0.0, input_tokens=0, unpriced=[])
    for c in cases:
        p = baseline.PRICES.get(c['model'])
        tokens = c['recorded_tokens'] or c['chars'] / 3.5
        if p is None:
            total['unpriced'].append(c['id'])
            continue
        mx = (c['body'] or {}).get('max_tokens', DEFAULT_MAX_TOKENS)
        out = c.get('recorded_output') or mx
        total['input_tokens'] += tokens
        total['expected'] += BATCH_DISCOUNT * (tokens * p['input'] + out * p['output']) / 1e6
        total['worst'] += BATCH_DISCOUNT * (tokens * p['input'] + mx * p['output']) / 1e6
    return total


def select(cases, kind, count):
    exact = [c for c in cases if c['kind'] == kind and c['usable'] and c['source'] == 'body']
    skipped = sum(1 for c in cases if c['kind'] == kind and c['source'] != 'body')
    n = min(count, len(exact))
    return [exact[int(i * len(exact) / n)] for i in range(n)], skipped


def judge(case, reply_text):
    """(agrees with the original, blockers missed, replay signature, original signature)."""
    original = replay.signature(case['kind'], replay.parse_json(case['original_text']))
    sig = replay.signature(case['kind'], replay.parse_json(reply_text))
    missed = replay.missed_blockers(original, sig) if case['kind'] in replay.FINDING_KINDS and sig and original else []
    return sig is not None and sig == original, missed, sig, original


def api_submit(requests):
    """The Batch API: create, poll until it ends, read the results. {custom_id: reply text}. Never run by the tests."""
    key = os.environ.get('ANTHROPIC_API_KEY')
    if not key:
        raise SystemExit('ANTHROPIC_API_KEY is not set')
    base = 'https://api.anthropic.com/v1/messages/batches'
    headers = {'x-api-key': key, 'anthropic-version': '2023-06-01', 'content-type': 'application/json'}

    def call(url, data=None):
        req = urllib.request.Request(url, data=None if data is None else json.dumps(data).encode(), headers=headers)
        with urllib.request.urlopen(req, timeout=120) as r:
            return r.read().decode('utf-8')
    batch = json.loads(call(base, {'requests': requests}))
    while batch.get('processing_status') != 'ended':
        time.sleep(30)
        batch = json.loads(call('%s/%s' % (base, batch['id'])))
    out = {}
    for line in call(batch['results_url']).splitlines():
        r = json.loads(line)
        msg = (r.get('result') or {}).get('message') or {}
        out[r['custom_id']] = ''.join(b.get('text', '') for b in msg.get('content', []) if b.get('type') == 'text')
    return out


def main(argv=None, submit=None, stdout=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('paths', nargs='+')
    ap.add_argument('--kinds', required=True, help='comma-separated: %s' % ', '.join(replay.KINDS))
    ap.add_argument('--new-effort', default='medium', choices=['low', 'medium', 'high'])
    ap.add_argument('--count', type=int, default=5, help='exact requests per kind')
    ap.add_argument('--approve', action='append', default=[], metavar='KIND',
                    help='send this kind\'s batch; one flag per kind, repeatable')
    ap.add_argument('--dry-run', action='store_true', help='print the estimate and what would be sent, send nothing')
    a = ap.parse_args(argv)
    out = stdout or sys.stdout
    kinds = [k for k in a.kinds.split(',') if k]
    for k in kinds + a.approve:
        if k not in replay.KINDS:
            ap.error('unknown kind %s; kinds are %s' % (k, ', '.join(replay.KINDS)))
    stray = [k for k in a.approve if k not in kinds]
    if stray:
        ap.error('--approve %s is not in --kinds' % ', '.join(stray))

    cases = []
    for folder in replay.find_missions(a.paths):
        cases.extend(replay.load_cases(folder))
    cases = [c for c in replay.check_rebuilds(cases) if c['kind'] in kinds]

    plan = {}
    for k in kinds:
        chosen, skipped = select(cases, k, a.count)
        plan[k] = chosen
        e = estimate(chosen, a.new_effort)
        out.write('%s: %d exact request(s) at %s effort (%d without a saved body skipped)\n' % (k, len(chosen), a.new_effort, skipped))
        out.write('  estimated cost $%.2f (worst case $%.2f), %d input tokens at the full input rate, %d%% batch discount\n' % (
            e['expected'], e['worst'], e['input_tokens'], 100 - 100 * BATCH_DISCOUNT))
        for cid in e['unpriced']:
            out.write('  no price for the model of %s: add it to PRICES in baseline.py; it is not in the estimate\n' % cid)
    approved = [k for k in kinds if k in a.approve and plan[k]]
    for k in kinds:
        if k not in a.approve:
            out.write('%s: not approved, nothing sent. Pass --approve %s to send it.\n' % (k, k))
    if a.dry_run:
        out.write('Dry run: nothing sent.\n')
        return 0
    if not approved:
        return 2
    for k in approved:
        requests = [batch_request(c, a.new_effort) for c in plan[k]]
        replies = (submit or api_submit)(requests)
        agree, bad = 0, 0
        out.write('%s results:\n' % k)
        for c in plan[k]:
            ok, missed, sig, original = judge(c, replies.get(c['id'].replace('/', '_'), ''))
            agree += ok
            bad += bool(missed)
            if not ok:
                out.write('  DISAGREES %s: original %s; replay %s%s\n' % (
                    c['id'], json.dumps(original, sort_keys=True), json.dumps(sig, sort_keys=True),
                    ('; MISSED BLOCKERS ' + ', '.join(missed)) if missed else ''))
        out.write('  %d of %d agree with the originals; %d missed a Blocker. %s\n' % (
            agree, len(plan[k]), bad, 'CONFIRMED' if agree == len(plan[k]) and not bad else 'NOT CONFIRMED: read the disagreements'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
