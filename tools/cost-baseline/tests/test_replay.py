"""Tests for the replay screen. Nothing here calls claude or the network: the runner is a fake."""
import io
import json
import os
import re
import tempfile
import unittest
from unittest import mock

import replay
from tests import fixtures as fx


def turn_of(req):
    return int(re.findall(r'Facts for turn (\d+)', req['prompt'])[-1])


class FakeRunner:
    """Answers each request with `answer(request)`, which defaults to echoing the original; records every call."""

    def __init__(self, answer):
        self.answer = answer
        self.calls = []

    def __call__(self, req):
        self.calls.append(req)
        reply = self.answer(req)
        if isinstance(reply, Exception):
            raise reply
        return reply if isinstance(reply, str) else json.dumps(reply)


class MissionCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = self._tmp.name
        self.turns = [
            ('debrief', fx.debrief('Nominal'), 1.0, False),         # 1
            ('debrief', fx.debrief('Off-Nominal'), 1.0, False),     # 2
            ('debrief', fx.debrief('Nominal'), 1.0, True),          # 3, saved body
            ('change', fx.plan('A', 'B'), 1.0, False),              # 4
            ('reply', fx.answer(), 1.0, False),                     # 5
            ('settings', fx.plan('A'), 1.0, False),                 # 6
            ('debrief', fx.debrief('Nominal'), 0.4, False),         # 7, recorded tokens far from the rebuild
            ('debrief', fx.debrief('Nominal'), None, False),        # 8, no stream dump
        ]
        self.folder = fx.make_mission(self.root, 'm1', self.turns)

    def cases(self, folder=None):
        return replay.check_rebuilds(replay.load_cases(folder or self.folder))

    def by_turn(self, cases):
        return {int(c['id'].split('-')[-1]) // 3: c for c in cases}


class RebuildTokenCheckTests(MissionCase):
    def test_requests_within_tolerance_are_usable_and_the_rest_unusable_with_a_reason(self):
        cases = self.cases()
        usable = sorted(c['kind'] for c in cases if c['usable'])
        self.assertEqual(['change', 'debrief', 'debrief', 'debrief', 'reply', 'settings'], usable)
        unusable = [c for c in cases if not c['usable']]
        self.assertEqual(2, len(unusable))
        reasons = sorted(c['reason'] for c in unusable)
        self.assertTrue(reasons[0].startswith('rebuilt request is +150% off the recorded input tokens'), reasons)
        self.assertTrue(reasons[1].startswith('rebuilt, with no recorded input tokens'), reasons)

    def test_a_tighter_tolerance_turns_a_near_miss_into_unusable(self):
        # Turn 2's recorded tokens are 10% under what the rebuild says: inside 15%, outside 5%.
        sse = os.path.join(self.folder, 'stream-dumps', 'request-20261026-100000-2.sse')
        with open(sse) as f:
            text = f.read()
        old = re.search(r'"cache_read_input_tokens": (\d+)', text).group(1)
        fx.write(sse, text.replace(old, str(int(int(old) * 0.9))))
        self.assertTrue(self.by_turn(self.cases())[2]['usable'])
        tight = self.by_turn(replay.check_rebuilds(replay.load_cases(self.folder), tolerance=0.05))
        self.assertFalse(tight[2]['usable'])
        self.assertIn('off the recorded input tokens (tolerance 5%)', tight[2]['reason'])

    def test_too_few_recorded_requests_to_calibrate_is_unusable(self):
        folder = fx.make_mission(self.root, 'm2', self.turns[:2])
        cases = self.cases(folder)
        self.assertTrue(all(not c['usable'] and 'too few recorded requests' in c['reason'] for c in cases))

    def test_a_saved_body_is_used_as_it_was_sent(self):
        c = self.by_turn(self.cases())[3]
        self.assertEqual('body', c['source'])
        self.assertEqual({'type': 'object', 'x': 'saved'}, c['schema'])
        self.assertEqual('claude-sonnet-5-5', c['model'])
        rebuilt = self.by_turn(self.cases())[2]
        self.assertEqual('transcript', rebuilt['source'])
        self.assertEqual('high', rebuilt['effort'])

    def test_turns_are_sorted_into_kinds_from_the_mod_notice(self):
        kinds = [c['kind'] for _, c in sorted(self.by_turn(self.cases()).items())]
        self.assertEqual(['debrief', 'debrief', 'debrief', 'change', 'reply', 'settings', 'debrief', 'debrief'], kinds)


class AgreementTests(MissionCase):
    def run_screen(self, answer, **kw):
        runner = FakeRunner(answer)
        cases = self.cases()
        results = replay.run(cases, runner, **kw)
        return runner, results, replay.summarize(results)

    def echo(self, req):
        n = turn_of(req)
        return json.loads(self.original_text(n))

    def original_text(self, n):
        return json.dumps(self.turns[n - 1][1])

    def test_replays_each_request_at_the_old_and_the_new_effort_with_no_tools(self):
        runner, results, summary = self.run_screen(self.echo, per_kind=20)
        self.assertEqual({'high', 'medium'}, {r['effort'] for r in runner.calls})
        for req in runner.calls:
            self.assertNotIn('tools', req)
            self.assertEqual(1.0, req['max_budget_usd'])
            self.assertTrue(req['system'].startswith('You are Claudia'))
            self.assertIn('Facts for turn', req['prompt'])
            self.assertNotIn('output_config', req['prompt'])
            self.assertTrue(isinstance(req['schema'], dict))
        # 6 usable requests, each at 2 efforts.
        self.assertEqual(12, len(runner.calls))
        self.assertEqual({'old': 'high', 'new': 'medium'}, {w: summary['debrief'][w]['effort'] for w in ('old', 'new')})

    def test_the_history_is_flattened_into_the_prompt(self):
        runner, _, _ = self.run_screen(self.echo)
        prompt = next(r for r in runner.calls if turn_of(r) == 4)['prompt']
        self.assertIn('[mod notice]\nThe player wants a change', prompt)
        self.assertIn('[Claudia]\n' + self.original_text(3), prompt)
        self.assertTrue(prompt.endswith(replay.CLOSING))
        self.assertNotIn('Facts for turn 5', prompt)

    def test_agreement_is_per_kind_at_both_efforts_against_the_originals(self):
        def answer(req):
            n = turn_of(req)
            if req['effort'] == 'medium' and n == 1:
                return fx.debrief('Off-Nominal')            # a Debrief outcome differs
            if req['effort'] == 'medium' and n == 4:
                return fx.plan('A')                          # a change touches other sequences
            if req['effort'] == 'medium' and n == 5:
                return fx.plan('A')                          # a reply proposes a revision it did not
            if req['effort'] == 'medium' and n == 6:
                return fx.answer()                           # settings proposes no revision
            return self.echo(req)
        _, _, s = self.run_screen(answer)
        self.assertEqual((3, 3), (s['debrief']['old']['agree'], s['debrief']['old']['runs']))
        self.assertEqual((2, 3), (s['debrief']['new']['agree'], s['debrief']['new']['runs']))
        for kind in ('change', 'reply', 'settings'):
            self.assertEqual((1, 1), (s[kind]['old']['agree'], s[kind]['old']['runs']), kind)
            self.assertEqual((0, 1), (s[kind]['new']['agree'], s[kind]['new']['runs']), kind)
        self.assertFalse(any(s[k]['pass'] for k in ('change', 'reply', 'settings')))

    def test_signature_compares_parsed_fields_not_wording(self):
        a = dict(fx.debrief('Nominal'), say='one wording')
        b = dict(fx.debrief('Nominal'), say='quite another')
        self.assertEqual(replay.signature('debrief', a), replay.signature('debrief', b))
        self.assertNotEqual(replay.signature('debrief', a), replay.signature('debrief', fx.debrief('Failed')))
        self.assertEqual(replay.signature('change', fx.plan('B', 'A')), replay.signature('change', fx.plan('A', 'B')))
        self.assertEqual({'revised': False, 'sequences': []}, replay.signature('reply', fx.answer()))
        sp = {'kind': 'sequence_plan', 'sequence_plan': {'name': 'Ascent'}}
        self.assertEqual({'revised': True, 'sequences': ['Ascent']}, replay.signature('change', sp))

    def test_craft_check_and_margin_recheck_compare_blocker_and_warning_sets(self):
        self.assertEqual({'blockers': ['delta_v_short'], 'warnings': ['launch_twr_low', 'no_landing_legs']},
                         replay.signature('craft_check', fx.findings('no_landing_legs', 'delta_v_short', 'launch_twr_low')))
        self.assertEqual(replay.signature('margin_recheck', fx.findings('a_warning', 'delta_v_short')),
                         replay.signature('margin_recheck', fx.findings('delta_v_short', 'a_warning', 'a_warning')))
        self.assertIsNone(replay.signature('craft_check', {'nothing': 1}))

    def test_a_failed_call_or_non_json_reply_counts_as_a_disagreement_and_is_reported(self):
        def answer(req):
            if req['effort'] == 'medium':
                return RuntimeError('rate limited') if turn_of(req) == 1 else 'not json'
            return self.echo(req)
        _, results, s = self.run_screen(answer, per_kind=1)
        new = [r for r in results if r['which'] == 'new']
        self.assertEqual(['RuntimeError: rate limited'], [r['error'] for r in new if 'Runtime' in (r['error'] or '')])
        self.assertTrue(all(not r['agree'] for r in new))
        self.assertEqual(0, s['debrief']['new']['agree'])


class PassRuleTests(MissionCase):
    def one_debrief(self):
        # One usable Debrief request, replayed 20 times at each effort: a miss is exactly 1 in 20.
        return [c for c in self.cases() if c['kind'] == 'debrief' and c['id'].endswith('turn-9')]

    def screen(self, old_misses, new_misses):
        count = {'high': 0, 'medium': 0}

        def answer(req):
            count[req['effort']] += 1
            misses = old_misses if req['effort'] == 'high' else new_misses
            return fx.debrief('Failed') if count[req['effort']] <= misses else fx.debrief('Nominal')
        results = replay.run(self.one_debrief(), FakeRunner(answer), repeats=20)
        return replay.summarize(results)['debrief']

    def test_one_in_twenty_worse_than_the_old_effort_still_passes(self):
        s = self.screen(0, 1)
        self.assertEqual((20, 19), (s['old']['agree'], s['new']['agree']))
        self.assertTrue(s['pass'], s['reasons'])

    def test_two_in_twenty_worse_fails(self):
        s = self.screen(0, 2)
        self.assertFalse(s['pass'])
        self.assertEqual(['agreement fell by more than 1 in 20'], s['reasons'])

    def test_the_old_efforts_own_variation_is_the_yardstick(self):
        self.assertTrue(self.screen(5, 6)['pass'])
        self.assertFalse(self.screen(5, 7)['pass'])

    def test_a_better_new_effort_passes(self):
        self.assertTrue(self.screen(3, 0)['pass'])

    def test_any_blocker_missed_fails_even_when_agreement_is_no_worse(self):
        folder = os.path.join(self.root, 'cc')
        fx.make_craft_check(folder, 'check-1', 'craft_check', fx.findings('delta_v_short', 'no_landing_legs'))
        cases = replay.check_rebuilds(replay.load_cases(folder))
        self.assertEqual(['craft_check'], [c['kind'] for c in cases])
        self.assertTrue(cases[0]['usable'])
        calls = {'high': 0, 'medium': 0}

        def answer(req):
            calls[req['effort']] += 1
            first = calls[req['effort']] == 1
            if req['effort'] == 'high':   # the old effort drifts on a Warning once
                return fx.findings('delta_v_short') if first else fx.findings('delta_v_short', 'no_landing_legs')
            return fx.findings('no_landing_legs') if first else fx.findings('delta_v_short', 'no_landing_legs')
        results = replay.run(cases, FakeRunner(answer), repeats=20)
        s = replay.summarize(results)['craft_check']
        self.assertEqual((19, 19), (s['old']['agree'], s['new']['agree']))
        self.assertEqual(1, s['new']['missed'])
        self.assertFalse(s['pass'])
        self.assertEqual(['1 run(s) missed a Blocker'], s['reasons'])

    def test_a_missed_warning_is_a_disagreement_but_not_a_missed_blocker(self):
        folder = os.path.join(self.root, 'cc')
        fx.make_craft_check(folder, 'recheck-1', 'margin_recheck', fx.findings('delta_v_short', 'no_landing_legs'))
        cases = replay.check_rebuilds(replay.load_cases(folder))
        def answer(req):
            both = fx.findings('delta_v_short', 'no_landing_legs')
            return both if req['effort'] == 'high' else fx.findings('delta_v_short')
        results = replay.run(cases, FakeRunner(answer), repeats=20)
        s = replay.summarize(results)['margin_recheck']
        self.assertEqual(0, s['new']['missed'])
        self.assertEqual((20, 0), (s['old']['agree'], s['new']['agree']))
        self.assertEqual(['agreement fell by more than 1 in 20'], s['reasons'])   # the disagreement fails it, not a Blocker

    def test_an_old_effort_that_never_reproduces_the_originals_is_no_yardstick(self):
        s = replay.summarize(replay.run(self.cases(), FakeRunner(lambda req: 'nope')))
        self.assertTrue(all('no yardstick' in ' '.join(S['reasons']) for S in s.values()))


class ReportTests(MissionCase):
    def build(self, answer):
        cases = self.cases()
        results = replay.run(cases, FakeRunner(answer))
        return cases, results, replay.summarize(results)

    def test_the_report_prints_every_disagreement_and_the_verdict_per_kind(self):
        def answer(req):
            n = turn_of(req)
            if req['effort'] == 'medium' and n in (1, 2):
                return fx.debrief('Failed')
            if req['effort'] == 'medium' and n == 4:
                return fx.plan('Z')
            return json.loads(json.dumps(self.turns[n - 1][1]))
        cases, results, summary = self.build(answer)
        text = replay.report(cases, results, summary, 'medium')
        section = text.split('## Disagreements')[1]
        lines = [l for l in section.splitlines() if l.startswith('- ')]
        self.assertEqual(sum(not r['agree'] for r in results), len(lines))
        self.assertEqual(3, len(lines))
        self.assertIn('m1/turn-12', section)
        self.assertIn('"sequences": ["Z"]', section)
        self.assertRegex(text, r'\| debrief \| 3 \| high \| 3/3 \(100%\) \| medium \| 1/3 \(33%\) \| 0 \| \*\*FAIL\*\*')
        self.assertRegex(text, r'\| settings \| 1 \| high \| 1/1 \(100%\) \| medium \| 1/1 \(100%\) \| 0 \| \*\*PASS\*\*')

    def test_the_report_lists_unusable_requests_with_reasons(self):
        cases, results, summary = self.build(lambda req: self.turns[turn_of(req) - 1][1])
        text = replay.report(cases, results, summary, 'medium')
        self.assertIn('Requests found: 8; usable: 6', text)
        self.assertIn('off the recorded input tokens', text)
        self.assertIn('with no recorded input tokens', text)

    def test_unsure_because_is_counted_against_the_disagreements(self):
        def answer(req):
            n = turn_of(req)
            if n not in (1, 2, 3):
                return json.loads(json.dumps(self.turns[n - 1][1]))
            if req['effort'] == 'high':
                return dict(self.turns[n - 1][1], unsure_because='')
            outcome = {1: 'Failed', 2: 'Failed', 3: 'Nominal'}[n]
            reason = {1: 'the burn may have overshot', 2: '', 3: ''}[n]
            return dict(fx.debrief(outcome), unsure_because=reason)
        cases, results, summary = self.build(answer)
        d = summary['debrief']
        self.assertEqual(3, d['new']['with_unsure'])
        self.assertEqual(1, d['new']['unsure_set'])
        self.assertEqual(2, d['new']['dis_with_unsure'])
        self.assertEqual(1, d['new']['dis_flagged'])
        self.assertEqual(0, d['old']['dis_with_unsure'])
        text = replay.report(cases, results, summary, 'medium')
        self.assertIn('| debrief | medium | 3 | 1 | 2 | 1/2 (50%) |', text)
        self.assertIn('unsure_because: "the burn may have overshot"', text)

    def test_replies_without_the_field_are_left_out_of_the_unsure_counts(self):
        _, _, summary = self.build(lambda req: json.loads(json.dumps(self.turns[turn_of(req) - 1][1])))
        self.assertEqual(0, summary['debrief']['new']['with_unsure'])


class CommandLineTests(MissionCase):
    def run_main(self, *args, runner=None):
        out = io.StringIO()
        code = replay.main([self.root] + list(args), runner=runner, stdout=out)
        return code, out.getvalue()

    def test_dry_run_sends_nothing_and_says_what_it_would_do(self):
        def boom(req):
            raise AssertionError('dry run must not call the runner')
        code, text = self.run_main('--dry-run', '--repeats', '3', runner=boom)
        self.assertEqual(0, code)
        self.assertIn('Dry run: nothing is sent.', text)
        self.assertIn('- debrief: 3 request(s), 9 replay(s) at the old effort and 9 at medium', text)
        self.assertIn('Total Claude Code calls it would make: 36', text)
        self.assertIn('Unusable: m1/turn-', text)

    def test_the_exit_code_follows_the_verdicts(self):
        runner = FakeRunner(lambda req: json.loads(json.dumps(self.turns[turn_of(req) - 1][1])))
        code, text = self.run_main(runner=runner)
        self.assertEqual(0, code)
        self.assertIn('**PASS**', text)
        code, _ = self.run_main(runner=FakeRunner(lambda req: 'nope'))
        self.assertEqual(1, code)

    def test_new_and_old_effort_and_kind_selection(self):
        runner = FakeRunner(lambda req: json.loads(json.dumps(self.turns[turn_of(req) - 1][1])))
        self.run_main('--kinds', 'settings', '--new-effort', 'low', '--old-effort', 'medium', runner=runner)
        self.assertEqual([('medium', 'low')], [(runner.calls[0]['effort'], runner.calls[1]['effort'])])
        self.assertEqual(2, len(runner.calls))

    def test_per_kind_caps_the_requests_replayed(self):
        runner = FakeRunner(lambda req: json.loads(json.dumps(self.turns[turn_of(req) - 1][1])))
        self.run_main('--kinds', 'debrief', '--per-kind', '2', runner=runner)
        self.assertEqual(4, len(runner.calls))

    def test_the_default_runner_is_claude_print_mode_with_no_tools_and_a_spend_cap(self):
        seen = {}

        def fake_run(cmd, **kw):
            seen['cmd'], seen['input'] = cmd, kw['input']
            return mock.Mock(stdout=json.dumps({'structured_output': {'kind': 'answer'}}))
        req = dict(model='claude-sonnet-5-5', effort='medium', system='S', prompt='P', schema={'type': 'object'},
                   max_budget_usd=0.5)
        with mock.patch.object(replay.subprocess, 'run', fake_run):
            self.assertEqual({'kind': 'answer'}, json.loads(replay.claude_print_runner(req)))
        cmd = seen['cmd']
        self.assertEqual(['claude', '-p'], cmd[:2])
        self.assertEqual('medium', cmd[cmd.index('--effort') + 1])
        self.assertEqual('', cmd[cmd.index('--tools') + 1])
        self.assertEqual('0.5', cmd[cmd.index('--max-budget-usd') + 1])
        self.assertEqual('S', cmd[cmd.index('--system-prompt') + 1])
        self.assertEqual('P', seen['input'])


class BlockerListTests(unittest.TestCase):
    def test_blockers_match_the_mods_craft_check_rules(self):
        path = os.path.join(os.path.dirname(__file__), '..', '..', '..', 'src', 'ClaudiaKermin.Core', 'Missions',
                            'CraftCheckRules.cs')
        if not os.path.exists(path):
            self.skipTest('CraftCheckRules.cs not found')
        with open(path, encoding='utf-8') as f:
            src = f.read()
        blockers = set(re.findall(r'new CraftCheckRule\("(\w+)", Blocker', src))
        self.assertTrue(blockers)
        self.assertEqual(blockers, set(replay.BLOCKERS))


if __name__ == '__main__':
    unittest.main()
