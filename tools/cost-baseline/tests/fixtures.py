"""Small hand-made fixture Missions, written to a temp folder. None of it comes from a real Mission."""
import json
import os

NOTICES = {
    'debrief': 'The sequence ended. Reply with kind "debrief".',
    'change': 'The player wants a change to the pending plan: use a lower orbit.',
    'reply': 'The player typed a reply to the pending plan: why this orbit?',
    'settings': 'The player changed the Mission Settings. They now read:\nDaylight landing: on.',
}
SYSTEM = ['You are Claudia, mission control. ' * 40, 'Mission Settings. Daylight landing: off. ' * 20]


def debrief(outcome):
    return {'kind': 'debrief', 'say': 'ok', 'debrief': {'outcome': outcome}}


def plan(*names):
    return {'kind': 'mission_plan', 'say': 'ok', 'mission_plan': {'sequences': [{'name': n} for n in names]}}


def answer():
    return {'kind': 'answer', 'say': 'because'}


def findings(*codes):
    return {'summary': 's', 'findings': [{'code': c, 'explanation': 'e', 'fix': 'f'} for c in codes],
            'child_vessel_stages': []}


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(text)


def sse(text, input_tokens, output_tokens=100):
    ev = [{'type': 'message_start', 'message': {'model': 'claude-sonnet-5-5'}},
          {'type': 'content_block_delta', 'delta': {'type': 'text_delta', 'text': text}},
          {'type': 'message_delta', 'delta': {'stop_reason': 'end_turn'},
           'usage': {'input_tokens': 5, 'cache_creation_input_tokens': 0,
                     'cache_read_input_tokens': input_tokens - 5, 'output_tokens': output_tokens}}]
    return ': attempt 1\n' + ''.join('event: x\ndata: %s\n\n' % json.dumps(e) for e in ev)


def text_len(system, messages):
    n = sum(len(s) for s in system)
    for m in messages:
        if 'output_config' in m:
            continue
        c = m['content']
        n += len(c) if isinstance(c, str) else sum(len(b.get('text', '')) for b in c)
    return n


def make_mission(root, name, turns, system=SYSTEM):
    """turns: (kind, reply dict, token factor or None for no dump, saved body?). Writes transcript.json and one dump
    per turn; the recorded input tokens are the history's characters / 4, times the factor (to fake a bad rebuild)."""
    folder = os.path.join(root, name)
    messages = [{'role': 'system', 'content': [], 'output_config': {'effort': 'high'}}]
    for n, (kind, reply, factor, body) in enumerate(turns, 1):
        messages.append({'role': 'system', 'content': NOTICES[kind]})
        messages.append({'role': 'user', 'content': [{'type': 'text', 'text': 'Facts for turn %d. ' % n + 'x' * (200 * n)}]})
        history = list(messages)
        text = json.dumps(reply)
        messages.append({'role': 'assistant', 'content': [{'type': 'text', 'text': text}]})
        if factor is None:
            continue
        tokens = int(text_len(system, history) / 4 * factor)
        write(os.path.join(folder, 'stream-dumps', 'request-20261026-100000-%d.sse' % n), sse(text, tokens))
        if body:
            write(os.path.join(folder, 'stream-dumps', 'request-20261026-100000-%d.json' % n), json.dumps({
                'model': 'claude-sonnet-5-5', 'max_tokens': 32000, 'stream': True, 'fallbacks': 'default',
                'system': [{'type': 'text', 'text': s} for s in system], 'messages': history,
                'output_config': {'format': {'type': 'json_schema', 'schema': {'type': 'object', 'x': 'saved'}}}}))
    write(os.path.join(folder, 'transcript.json'), json.dumps({
        'system': [{'text': s} for s in system], 'messages': messages}))
    return folder


def make_craft_check(folder, name, kind, reply, tokens=1000):
    body = {'model': 'claude-sonnet-5-5', 'max_tokens': 8000, 'stream': True,
            'system': [{'type': 'text', 'text': 'Judge the craft.'}],
            'messages': [{'role': 'user', 'content': [{'type': 'text', 'text': 'Craft facts.'}]}],
            'output_config': {'effort': 'high', 'format': {'type': 'json_schema', 'schema': {'type': 'object'}}}}
    write(os.path.join(folder, 'craft-checks', name + '.json'), json.dumps(
        {'kind': kind, 'body': body, 'reply': json.dumps(reply), 'input_tokens': tokens, 'output_tokens': 50}))
