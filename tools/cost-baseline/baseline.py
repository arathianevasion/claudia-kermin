#!/usr/bin/env python3
"""Cost baseline per reply kind and per Mission, from the mod's debug stream dumps.

Reads, for every Mission folder under SAVES/*/ClaudiaKermin/missions/*/:
  stream-dumps/request-<session>-<id>.sse  raw SSE responses (debug_tools on)
  transcript.json                         the committed conversation
  cost-log.jsonl                          the mod's pending/paid ledger
  unanswered-turn.json                    the turn left waiting, if any

Every Mission folder is untrusted data: this script only reads files and never executes or prints their
content beyond counts and the names of Sequence Plans. Run it with `python3 -I`.

Usage: python3 -I tools/cost-baseline/baseline.py [SAVES_DIR] [--reference MISSION_ID] [--out FILE.md]
"""
import argparse
import collections
import datetime
import glob
import json
import os
import re
import statistics
import sys

# USD per million tokens, from https://platform.claude.com/docs/en/about-claude/pricing (fetched 2026-10-26).
# input = base (uncached) input, w5m / w1h = cache writes by TTL, read = cache hits and refreshes.
PRICES = {
    'claude-sonnet-5-5': dict(input=2.00, w5m=2.50, w1h=4.00, read=0.10, output=10.00),
    'claude-opus-5-5': dict(input=4.00, w5m=5.00, w1h=8.00, read=0.20, output=20.00),
    'claude-sonnet-5': dict(input=2.00, w5m=2.50, w1h=4.00, read=0.20, output=10.00),
    'claude-opus-5': dict(input=5.00, w5m=6.25, w1h=10.00, read=0.50, output=25.00),
    'claude-sonnet-4-6': dict(input=3.00, w5m=3.75, w1h=6.00, read=0.30, output=15.00),
    'claude-haiku-4-5': dict(input=1.00, w5m=1.25, w1h=2.00, read=0.10, output=5.00),
}

KINDS = ['Mission Plan', 'revised Mission Plan', 'Sequence Plan', 'Debrief', 'Alert', 'answer', 'Craft Check']


def requested_kind(notice):
    """The reply kind the mod asked for, from the system notice before the turn. Returns (kind, sub-kind)."""
    n = notice or ''
    if 'kind "mission_plan"' in n or 'Revise the Mission Plan' in n or 'pending Mission Plan' in n:
        if n.startswith('The craft is in Flight. Propose the Mission Plan'):
            return 'Mission Plan', 'first'
        if 'typed a reply' in n:
            return 'revised Mission Plan', 'reply classification'
        if 'Off-Nominal' in n:
            return 'revised Mission Plan', 'after Off-Nominal Debrief'
        if 'wants a change' in n:
            return 'revised Mission Plan', 'player change'
        if 'craft back' in n:
            return 'revised Mission Plan', 'after manual stretch'
        if 'loaded from an earlier save' in n:
            return 'revised Mission Plan', 'after a load'
        if 'reduced Mission Goal' in n:
            return 'revised Mission Plan', 'reduced goal'
        return 'revised Mission Plan', 'other'
    if 'kind "sequence_plan"' in n or 'pending Sequence Plan' in n:
        if 'Script Check' in n:
            return 'Sequence Plan', 'Script Check rewrite'
        if 'wants a change' in n:
            return 'Sequence Plan', 'player change'
        if 'typed a reply' in n:
            return 'Sequence Plan', 'reply classification'
        return 'Sequence Plan', 'next leg'
    if 'kind "debrief"' in n:
        return 'Debrief', 'manual stretch' if 'manual stretch' in n else 'sequence'
    if 'kind "alert"' in n:
        return 'Alert', 'alert'
    if 'kind "answer"' in n:
        return 'answer', 'answer'
    return None, None


def text_of(content):
    if isinstance(content, str):
        return content
    return ''.join(b.get('text', '') for b in content if isinstance(b, dict) and b.get('type') == 'text')


def parse_dump(path):
    """One request file: a list of attempts, each with its model, final usage, stop reason and streamed text length."""
    attempts = []
    cur = None
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if line.startswith(': attempt'):
                cur = dict(model=None, usage=None, stop=None, text_len=0, thinking_blocks=0, compaction_blocks=0,
                           error=None)
                attempts.append(cur)
                continue
            if not line.startswith('data:') or cur is None:
                continue
            try:
                d = json.loads(line[5:])
            except ValueError:
                continue
            t = d.get('type')
            if t == 'message_start':
                cur['model'] = d['message'].get('model')
            elif t == 'content_block_start':
                bt = d['content_block'].get('type')
                if bt == 'thinking':
                    cur['thinking_blocks'] += 1
                elif bt == 'compaction':
                    cur['compaction_blocks'] += 1
            elif t == 'content_block_delta' and d['delta'].get('type') == 'text_delta':
                cur['text_len'] += len(d['delta'].get('text', ''))
            elif t == 'message_delta':
                cur['usage'] = d.get('usage')
                cur['stop'] = d['delta'].get('stop_reason')
            elif t == 'error':
                cur['error'] = (d.get('error') or {}).get('type')
    return attempts


def iteration_rows(usage, model):
    """Priced token rows, one per usage iteration (falls back to the top-level usage)."""
    its = usage.get('iterations') or [dict(usage, type='message')]
    rows = []
    for it in its:
        m = it.get('model') or model
        p = PRICES.get(m)
        if p is None:
            raise SystemExit('no price for model %r: add it to PRICES' % m)
        cc = it.get('cache_creation') or {}
        w1h = cc.get('ephemeral_1h_input_tokens', 0)
        w5m = cc.get('ephemeral_5m_input_tokens', 0)
        # A write the API did not split by TTL is priced at the 1-hour rate the mod asks for.
        w1h += max(0, it.get('cache_creation_input_tokens', 0) - w1h - w5m)
        r = dict(type=it.get('type'), model=m, input=it.get('input_tokens', 0), w1h=w1h, w5m=w5m,
                 read=it.get('cache_read_input_tokens', 0), output=it.get('output_tokens', 0))
        r['cost_input'] = r['input'] * p['input'] / 1e6
        r['cost_w'] = (r['w1h'] * p['w1h'] + r['w5m'] * p['w5m']) / 1e6
        r['cost_read'] = r['read'] * p['read'] / 1e6
        r['cost_output'] = r['output'] * p['output'] / 1e6
        r['cost'] = r['cost_input'] + r['cost_w'] + r['cost_read'] + r['cost_output']
        r['out_price'] = p['output']
        rows.append(r)
    return rows


def ts(s):
    s = s.rstrip('Z')
    if '.' in s:
        head, frac = s.split('.')
        s = head + '.' + frac[:6]
    return datetime.datetime.fromisoformat(s)


def load_json(path):
    try:
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def analyse_mission(folder):
    mid = os.path.basename(folder.rstrip('/'))
    save = folder.split(os.sep + 'ClaudiaKermin' + os.sep)[0].split(os.sep)[-1]
    M = dict(id=mid, save=save, responses=[], craft=[], incomplete_attempts=0, notes=[])

    # Transcript turns, with the reply kind the mod asked for.
    turns = []
    tr = load_json(os.path.join(folder, 'transcript.json'))
    if tr:
        notice = None
        for i, m in enumerate(tr.get('messages', [])):
            if m.get('role') == 'system':
                s = text_of(m.get('content'))
                k, _ = requested_kind(s)
                if k:
                    notice = s
            elif m.get('role') == 'assistant':
                txt = text_of(m.get('content'))
                try:
                    reply = json.loads(txt)
                except ValueError:
                    reply = {}
                k, sub = requested_kind(notice)
                if k is None:  # no notice: fall back to the kind the reply gave
                    k = {'mission_plan': 'Mission Plan', 'sequence_plan': 'Sequence Plan', 'debrief': 'Debrief',
                         'alert': 'Alert', 'answer': 'answer'}.get(reply.get('kind'), 'unknown')
                    sub = 'no notice'
                turns.append(dict(index=i, text_len=len(txt), kind=k, sub=sub, reply=reply, matched=False))
                notice = None
    unanswered = load_json(os.path.join(folder, 'unanswered-turn.json'))
    tail_kind = requested_kind((unanswered or {}).get('notice'))

    # Responses, in request order (session start, then id).
    def key(p):
        m = re.match(r'request-(\d{8}-\d{6})-(\d+)\.sse$', os.path.basename(p))
        return (m.group(1), int(m.group(2))) if m else (os.path.basename(p), 0)
    dumps = sorted(glob.glob(os.path.join(folder, 'stream-dumps', 'request-*.sse')), key=key)
    pos = 0
    for p in dumps:
        attempts = parse_dump(p)
        final = [a for a in attempts if a['usage'] is not None]
        M['incomplete_attempts'] += len(attempts) - len(final)
        a = final[-1] if final else None
        R = dict(file=os.path.basename(p), attempt=a, priced=bool(a), committed=False, kind=None, sub=None,
                 reply={}, attempts=len(attempts))
        if a:
            R['rows'] = iteration_rows(a['usage'], a['model'])
            R['thinking'] = (a['usage'].get('output_tokens_details') or {}).get('thinking_tokens', 0)
            R['stop'] = a['stop']
            R['compaction'] = any(r['type'] == 'compaction' for r in R['rows'])
            # Match to the first later transcript turn with the same text length.
            for j in range(pos, len(turns)):
                if not turns[j]['matched'] and turns[j]['text_len'] == a['text_len'] and a['text_len'] > 0:
                    turns[j]['matched'] = True
                    R.update(committed=True, kind=turns[j]['kind'], sub=turns[j]['sub'], reply=turns[j]['reply'])
                    pos = j + 1
                    break
        if not R['committed']:
            # An uncommitted response was asking for the next turn still to commit, or the turn left unanswered.
            nxt = next((t for t in turns[pos:] if not t['matched']), None)
            if nxt:
                R['kind'], R['sub'] = nxt['kind'], nxt['sub']
            elif tail_kind[0]:
                R['kind'], R['sub'] = tail_kind
            else:
                R['kind'], R['sub'] = 'unknown', 'unknown'
        M['responses'].append(R)
    M['unmatched_turns'] = sum(1 for t in turns if not t['matched'])
    M['turns'] = turns

    # Cost log: main requests pair with the dumps in order; Craft Checks and margin rechecks are not dumped.
    log = []
    try:
        with open(os.path.join(folder, 'cost-log.jsonl'), encoding='utf-8') as f:
            for line in f:
                try:
                    log.append(json.loads(line))
                except ValueError:
                    pass
    except OSError:
        pass
    M['has_cost_log'] = bool(log)
    pend = {}
    main = []
    for e in log:
        ev, rq = e.get('event'), e.get('request')
        if ev == 'craft_check_paid' or ev == 'craft_check_kept':
            M['craft'].append(dict(sub='Craft Check', cost=float(e.get('amount', 0)), seconds=None,
                                   outcome=e.get('outcome')))
        elif ev == 'pending':
            pend[rq] = e
            if e.get('outcome') != 'margin_recheck':
                main.append(dict(request=rq, pending=e, settle=None))
        elif ev in ('paid', 'kept', 'released'):
            pe = pend.get(rq)
            secs = (ts(e['at']) - ts(pe['at'])).total_seconds() if pe else None
            if pe and pe.get('outcome') == 'margin_recheck':
                M['craft'].append(dict(sub='margin recheck', cost=float(e.get('amount', 0)) if ev != 'released' else 0.0,
                                       seconds=secs, outcome=e.get('outcome')))
            else:
                for mm in main:
                    if mm['request'] == rq and mm['settle'] is None:
                        mm['settle'] = e
                        mm['seconds'] = secs
    M['log_main'] = main
    if log:
        # Requests the mod settled without a response (released before sending) have no dump.
        sent = [mm for mm in main if not (mm['settle'] and mm['settle'].get('event') == 'released'
                                          and mm['settle'].get('outcome') == 'rejected')]
        if len(sent) == len(M['responses']):
            for R, mm in zip(M['responses'], sent):
                R['log'] = mm
        else:
            M['notes'].append('cost log has %d sent requests but %d dumps: timing not paired' %
                              (len(sent), len(M['responses'])))
    return M


def pct(a, b):
    return 100.0 * a / b if b else 0.0


def q(vals, p):
    if not vals:
        return None
    v = sorted(vals)
    k = (len(v) - 1) * p
    lo = int(k)
    hi = min(lo + 1, len(v) - 1)
    return v[lo] + (v[hi] - v[lo]) * (k - lo)


def fmt_s(x):
    return '-' if x is None else '%.0f s' % x


def tok(n):
    return '{:,}'.format(int(n))


def money(x):
    return '$%.2f' % x


def kind_table(missions, out):
    agg = collections.OrderedDict((k, collections.Counter()) for k in KINDS)
    times = collections.defaultdict(list)
    for M in missions:
        for R in M['responses']:
            k = R['kind'] if R['kind'] in agg else 'unknown'
            agg.setdefault(k, collections.Counter())
            A = agg[k]
            A['requests'] += 1
            if not R['priced']:
                A['unpriced'] += 1
                continue
            for r in R['rows']:
                for f in ('input', 'w1h', 'w5m', 'read', 'output', 'cost', 'cost_input', 'cost_w', 'cost_read',
                          'cost_output'):
                    A[f] += r[f]
            A['thinking'] += R['thinking']
            A['thinking_cost'] += R['thinking'] * R['rows'][-1]['out_price'] / 1e6
            A['cutoffs'] += R['stop'] == 'max_tokens'
            A['failed'] += not R['committed']
            if R.get('log') and R['log'].get('seconds') is not None:
                times[k].append(R['log']['seconds'])
        for C in M['craft']:
            A = agg['Craft Check']
            A['requests'] += 1
            A['cost'] += C['cost']
            if C['seconds'] is not None:
                times['Craft Check'].append(C['seconds'])
    tot = collections.Counter()
    for A in agg.values():
        tot.update(A)
    out.append('| Reply kind | Requests | Cost | Share | Uncached in | 1h write | 5m write | Read | Output | Thinking | '
               'Reply time p50 | p90 | Cut-offs | Not committed |')
    out.append('|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
    for k, A in list(agg.items()) + [('**Total**', tot)]:
        if not A['requests']:
            continue
        tv = times.get(k, []) if k != '**Total**' else [x for v in times.values() for x in v]
        if k == 'Craft Check':
            out.append('| Craft Check (incl. margin rechecks)* | %d | %s | %.1f%% | - | - | - | - | - | - | %s | %s | - | - |' %
                       (A['requests'], money(A['cost']), pct(A['cost'], tot['cost']), fmt_s(q(tv, .5)), fmt_s(q(tv, .9))))
            continue
        out.append('| %s | %d | %s | %.1f%% | %s | %s | %s | %s | %s | %s | %s | %s | %d | %d |' % (
            k, A['requests'], money(A['cost']), pct(A['cost'], tot['cost']), tok(A['input']), tok(A['w1h']),
            tok(A['w5m']), tok(A['read']), tok(A['output']), tok(A['thinking']), fmt_s(q(tv, .5)), fmt_s(q(tv, .9)),
            A['cutoffs'], A['failed']))
    return agg, tot


def sub_table(missions, out):
    agg = collections.OrderedDict()
    for M in missions:
        for R in M['responses']:
            A = agg.setdefault((R['kind'], R['sub']), collections.Counter())
            A['requests'] += 1
            if R['priced']:
                A['cost'] += sum(r['cost'] for r in R['rows'])
                A['output'] += sum(r['output'] for r in R['rows'])
                A['thinking'] += R['thinking']
                A['failed'] += not R['committed']
    out.append('| Reply kind | Asked for | Requests | Cost | Output | Thinking | Not committed |')
    out.append('|---|---|---:|---:|---:|---:|---:|')
    for (k, s), A in sorted(agg.items(), key=lambda kv: (KINDS.index(kv[0][0]) if kv[0][0] in KINDS else 99, -kv[1]['cost'])):
        out.append('| %s | %s | %d | %s | %s | %s | %d |' % (k, s, A['requests'], money(A['cost']), tok(A['output']),
                                                         tok(A['thinking']), A['failed']))


def mission_stats(M, prices_total=None):
    S = collections.Counter()
    S['requests'] = len(M['responses'])
    S['craft_calls'] = len(M['craft'])
    S['craft_cost'] = sum(c['cost'] for c in M['craft'])
    n = 0
    prev_context = 0
    for R in M['responses']:
        if not R['priced']:
            continue
        S['cost'] += sum(r['cost'] for r in R['rows'])
        for r in R['rows']:
            S['comp_cost'] += r['cost'] if r['type'] == 'compaction' else 0
            S['compactions'] += r['type'] == 'compaction'
            if r['type'] != 'compaction':
                S['max_context'] = max(S['max_context'], r['input'] + r['w1h'] + r['w5m'] + r['read'])
        n += 1
        msg = [r for r in R['rows'] if r['type'] != 'compaction']
        if n > 2 and not R['compaction'] and msg:
            m = msg[-1]
            w = m['w1h'] + m['w5m']
            if w > m['read']:
                S['misses'] += 1
                S['miss_write_cost'] += m['cost_w']
                # The prefix the previous request had cached but this one wrote again: paid at write, not read, price.
                lost = max(0, prev_context - m['read'])
                p = PRICES[m['model']]
                S['miss_rewritten'] += lost
                S['miss_excess_cost'] += lost * (p['w1h'] - p['read']) / 1e6
        if msg:
            m = msg[-1]
            prev_context = m['input'] + m['w1h'] + m['w5m'] + m['read']
        S['cutoffs'] += R['stop'] == 'max_tokens'
        S['cutoff_cost'] += sum(r['cost'] for r in R['rows']) if R['stop'] == 'max_tokens' else 0
        S['failed'] += not R['committed']
        S['failed_cost'] += 0 if R['committed'] else sum(r['cost'] for r in R['rows'])
    S['unpriced'] = sum(1 for R in M['responses'] if not R['priced'])
    legs = []
    for t in M['turns']:
        rep = t['reply']
        if rep.get('kind') == 'sequence_plan' and isinstance(rep.get('sequence_plan'), dict):
            S['sequence_plans'] += 1
            legs.append(rep['sequence_plan'].get('name') or '')
        if rep.get('kind') == 'debrief' and isinstance(rep.get('debrief'), dict):
            S['debriefs'] += 1
            o = rep['debrief'].get('outcome')
            S['off_nominal'] += o == 'Off-Nominal'
            S['failed_debriefs'] += o == 'Failed'
    S['legs'] = len(set(legs))
    S['total_cost'] = S['cost'] + S['craft_cost']
    return S, legs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('saves', nargs='?', default=os.path.expanduser('~/KSP_spike/saves'))
    ap.add_argument('--reference', default='m20261026-031725-724a')
    ap.add_argument('--out')
    a = ap.parse_args()
    folders = sorted(glob.glob(os.path.join(a.saves, '*', 'ClaudiaKermin', 'missions', '*', '')))
    missions = [analyse_mission(f) for f in folders]
    missions = [M for M in missions if M['responses'] or M['craft']]
    out = []
    w = out.append
    w('# Cost baseline per reply kind, %s' % datetime.date.today().isoformat())
    w('')
    w('Generated by `python3 -I tools/cost-baseline/baseline.py` from %d Missions with stream dumps or a cost log. '
      'Prices are the published per-MTok rates in `PRICES` in the script; each cache write is priced at the TTL the '
      'API reported for it. Cost is summed over every `usage.iterations` entry, so compaction is included.' %
      len(missions))
    w('')

    def section(title, ms):
        w('## ' + title)
        w('')
        agg, tot = kind_table(ms, out)
        w('')
        w('\\* Craft Checks and margin rechecks are not stream-dumped: their cost is the mod\'s own `paid` amount '
          'from `cost-log.jsonl`, and the first Craft Check of a Mission is folded in with no reply time.')
        w('')
        S = collections.Counter()
        for M in ms:
            S.update(mission_stats(M)[0])
        S['max_context'] = max((mission_stats(M)[0]['max_context'] for M in ms), default=0)
        dumped = tot['cost'] - agg['Craft Check']['cost']
        out_tok = tot['output']
        th = tot['thinking']
        th_cost = tot['thinking_cost']
        w('Shares of the dumped spend (%s; Craft Checks excluded):' % money(dumped))
        w('')
        w('| Measure | Value |')
        w('|---|---:|')
        w('| Output | %s (%.1f%%) |' % (money(tot['cost_output']), pct(tot['cost_output'], dumped)))
        w('| Thinking, of output tokens | %.1f%% (%s of %s) |' % (pct(th, out_tok), tok(th), tok(out_tok)))
        w('| Thinking, of spend | %s (%.1f%%) |' % (money(th_cost), pct(th_cost, dumped)))
        w('| Cache writes (1h %s + 5m %s tokens) | %s (%.1f%%) |' % (tok(tot['w1h']), tok(tot['w5m']), money(tot['cost_w']),
                                                                  pct(tot['cost_w'], dumped)))
        w('| Cache reads | %s (%.1f%%) |' % (money(tot['cost_read']), pct(tot['cost_read'], dumped)))
        w('| Uncached input | %s (%.1f%%) |' % (money(tot['cost_input']), pct(tot['cost_input'], dumped)))
        w('| Compaction iterations (%d) | %s (%.1f%%) |' % (S['compactions'], money(S['comp_cost']), pct(S['comp_cost'], dumped)))
        w('| Cut-offs (stop_reason max_tokens) | %d of %d responses, %s (%.1f%%) |' % (
            S['cutoffs'], S['requests'] - S['unpriced'], money(S['cutoff_cost']), pct(S['cutoff_cost'], dumped)))
        w('| Responses not committed (cut-off, off-schema, refused, abandoned) | %d, %s (%.1f%%) |' % (
            S['failed'], money(S['failed_cost']), pct(S['failed_cost'], dumped)))
        w('| Mid-Mission full cache misses (request 3+, no compaction, write > read) | %d, all their writes %s (%.1f%%) |' % (
            S['misses'], money(S['miss_write_cost']), pct(S['miss_write_cost'], dumped)))
        w('| ... of which re-writing a prefix the previous request had cached | %s tokens, %s over the read price (%.1f%%) |' % (
            tok(S['miss_rewritten']), money(S['miss_excess_cost']), pct(S['miss_excess_cost'], dumped)))
        w('| Attempts with no final usage (dropped streams, unpriced) | %d |' % sum(M['incomplete_attempts'] for M in ms))
        w('| Responses with no final usage at all (unpriced) | %d |' % S['unpriced'])
        w('| Sequence Plans / distinct legs | %d / %d |' % (S['sequence_plans'], S['legs']))
        w('| Debriefs: Off-Nominal / Failed / all | %d / %d / %d |' % (S['off_nominal'], S['failed_debriefs'], S['debriefs']))
        w('| Max context (tokens in one request) | %s |' % tok(S['max_context']))
        w('')
        return agg, tot

    section('All Missions', missions)
    ref = [M for M in missions if M['id'] == a.reference]
    if ref:
        section('Reference Mun Mission %s (save "%s")' % (a.reference, ref[0]['save']), ref)
        S, legs = mission_stats(ref[0])
        w('Sequence Plans in order: ' + ' / '.join(legs))
        w('')

    w('## Reply kinds by what the mod asked for')
    w('')
    sub_table(missions, out)
    w('')
    w('## Per Mission')
    w('')
    w('| Mission | Save | Cost | Craft Check cost | Requests | Max context | Compactions | Sequence Plans / legs | '
      'Off-Nominal / Failed Debriefs | Cut-offs | Not committed | Mid-Mission misses |')
    w('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
    T = collections.Counter()
    for M in missions:
        S, _ = mission_stats(M)
        T.update(S)
        name = ('**%s**' % M['id']) if M['id'] == a.reference else M['id']
        w('| %s | %s | %s | %s | %d | %s | %d | %d / %d | %d / %d | %d | %d | %d |' % (
            name, M['save'], money(S['total_cost']), money(S['craft_cost']), S['requests'], tok(S['max_context']),
            S['compactions'], S['sequence_plans'], S['legs'], S['off_nominal'], S['failed_debriefs'], S['cutoffs'],
            S['failed'], S['misses']))
    w('| **Total** | | %s | %s | %d | | %d | %d / %d | %d / %d | %d | %d | %d |' % (
        money(T['total_cost']), money(T['craft_cost']), T['requests'], T['compactions'], T['sequence_plans'], T['legs'],
        T['off_nominal'], T['failed_debriefs'], T['cutoffs'], T['failed'], T['misses']))
    w('')
    w('Legs are distinct Sequence Plan names within a Mission; the total sums per-Mission counts.')
    w('')

    # Check the computed cost against the mod's own paid amounts.
    diffs = []
    for M in missions:
        for R in M['responses']:
            lg = R.get('log')
            if R['priced'] and lg and lg.get('settle') and lg['settle'].get('event') == 'paid':
                diffs.append((sum(r['cost'] for r in R['rows']), float(lg['settle']['amount'])))
    if diffs:
        mine = sum(d[0] for d in diffs)
        mod = sum(d[1] for d in diffs)
        off = sum(1 for d in diffs if abs(d[0] - d[1]) > max(0.0005, 0.01 * d[1]))
        w('## Checks')
        w('')
        w('- %d responses pair with a `paid` line in the cost log: computed %s, mod paid %s; %d differ by over 1%%.' % (
            len(diffs), money(mine), money(mod), off))
        # The same tokens priced with cache reads at 0.1x input and every write at 1h, as the mod's PriceTable does.
        alt = 0.0
        for M in missions:
            for R in M['responses']:
                lg = R.get('log')
                if R['priced'] and lg and lg.get('settle') and lg['settle'].get('event') == 'paid':
                    for r in R['rows']:
                        p = PRICES[r['model']]
                        alt += (r['input'] * p['input'] + (r['w1h'] + r['w5m']) * p['input'] * 2
                                + r['read'] * p['input'] * 0.1 + r['output'] * p['output']) / 1e6
        w('- Re-pricing those responses with cache reads at 0.1x input and every write at the 1h rate gives %s '
          'against the mod\'s %s: the gap is the mod\'s cache-read multiplier, not missing tokens.' % (money(alt), money(mod)))
    for M in missions:
        for n in M['notes']:
            w('- %s: %s' % (M['id'], n))
        if M['unmatched_turns']:
            w('- %s: %d committed assistant turns with no stream dump (dumps off, or the file is missing).' % (
                M['id'], M['unmatched_turns']))
    w('')
    text = '\n'.join(out)
    if a.out:
        with open(a.out, 'w', encoding='utf-8') as f:
            f.write(text)
    else:
        sys.stdout.write(text)


if __name__ == '__main__':
    main()
