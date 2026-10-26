"""Tests for the Batch API confirm script. Nothing here touches the network: `submit` is a fake."""
import contextlib
import io
import json
import tempfile
import unittest

import batch_confirm
import replay
from tests import fixtures as fx


class FakeSubmit:
    def __init__(self, answer=None):
        self.answer = answer
        self.sent = []

    def __call__(self, requests):
        self.sent.append(requests)
        out = {}
        for r in requests:
            if self.answer:
                out[r['custom_id']] = json.dumps(self.answer(r))
            else:
                out[r['custom_id']] = self.original(r)
        return out

    def original(self, r):
        # The original is whatever turn the request ends at: the saved bodies are of the Debriefs.
        n = int(r['custom_id'].split('-')[-1]) // 3
        return json.dumps(TURNS[n - 1][1])


TURNS = [
    ('debrief', fx.debrief('Nominal'), 1.0, True),
    ('debrief', fx.debrief('Off-Nominal'), 1.0, True),
    ('debrief', fx.debrief('Nominal'), 1.0, True),
    ('reply', fx.answer(), 1.0, True),
    ('reply', fx.answer(), 1.0, False),
    ('settings', fx.plan('A'), 1.0, False),
]


class BatchCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        fx.make_mission(self._tmp.name, 'm1', TURNS)

    def run_main(self, *args, submit=None):
        out = io.StringIO()
        code = batch_confirm.main([self._tmp.name] + list(args), submit=submit, stdout=out)
        return code, out.getvalue()


class ApprovalTests(BatchCase):
    def test_nothing_is_sent_without_an_approve_flag_and_the_cost_is_printed_first(self):
        submit = FakeSubmit()
        code, text = self.run_main('--kinds', 'debrief,reply', submit=submit)
        self.assertEqual(2, code)
        self.assertEqual([], submit.sent)
        self.assertIn('debrief: 3 exact request(s) at medium effort', text)
        self.assertRegex(text, r'estimated cost \$\d+\.\d\d \(worst case \$\d+\.\d\d\)')
        self.assertIn('debrief: not approved, nothing sent. Pass --approve debrief to send it.', text)
        self.assertIn('reply: not approved', text)

    def test_only_the_approved_kind_is_sent(self):
        submit = FakeSubmit()
        code, text = self.run_main('--kinds', 'debrief,reply', '--approve', 'debrief', submit=submit)
        self.assertEqual(0, code)
        self.assertEqual(1, len(submit.sent))
        self.assertEqual(3, len(submit.sent[0]))
        self.assertTrue(all(r['custom_id'].startswith('m1_turn-') for r in submit.sent[0]))
        self.assertIn('reply: not approved', text)
        self.assertNotIn('debrief: not approved', text)

    def test_the_estimate_is_printed_before_any_result(self):
        _, text = self.run_main('--kinds', 'debrief', '--approve', 'debrief', submit=FakeSubmit())
        self.assertLess(text.index('estimated cost'), text.index('debrief results:'))

    def test_an_approved_kind_with_nothing_exact_to_send_says_so(self):
        submit = FakeSubmit()
        code, text = self.run_main('--kinds', 'settings', '--approve', 'settings', submit=submit)
        self.assertEqual(2, code)
        self.assertEqual([], submit.sent)
        self.assertIn('settings: approved, but there are no exact requests to send.', text)

    def test_each_kind_needs_its_own_flag(self):
        submit = FakeSubmit()
        self.run_main('--kinds', 'debrief,reply', '--approve', 'debrief', '--approve', 'reply', submit=submit)
        self.assertEqual(2, len(submit.sent))

    def test_there_is_no_blanket_approval(self):
        for bad in ('all', '*', 'debriefs'):
            with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
                self.run_main('--kinds', 'debrief', '--approve', bad, submit=FakeSubmit())

    def test_approving_a_kind_not_asked_for_is_an_error(self):
        with self.assertRaises(SystemExit):
            with contextlib.redirect_stderr(io.StringIO()):
                self.run_main('--kinds', 'debrief', '--approve', 'reply', submit=FakeSubmit())

    def test_dry_run_never_sends_even_when_approved(self):
        submit = FakeSubmit()
        code, text = self.run_main('--kinds', 'debrief', '--approve', 'debrief', '--dry-run', submit=submit)
        self.assertEqual(0, code)
        self.assertEqual([], submit.sent)
        self.assertIn('Dry run: nothing sent.', text)
        self.assertIn('estimated cost', text)


class EstimateTests(BatchCase):
    def cases(self):
        return [c for c in replay.check_rebuilds(replay.load_cases(self._tmp.name + '/m1')) if c['kind'] == 'debrief']

    def test_estimate_prices_input_and_output_at_half_the_rates(self):
        cases = self.cases()
        e = batch_confirm.estimate(cases, 'medium')
        p = batch_confirm.baseline.PRICES['claude-sonnet-5-5']
        tokens = sum(c['recorded_tokens'] for c in cases)
        self.assertEqual(3, e['requests'])
        self.assertAlmostEqual(0.5 * (tokens * p['input'] + 3 * 100 * p['output']) / 1e6, e['expected'])
        self.assertAlmostEqual(0.5 * (tokens * p['input'] + 3 * 32000 * p['output']) / 1e6, e['worst'])
        self.assertGreater(e['worst'], e['expected'])

    def test_a_model_with_no_price_is_named_and_left_out(self):
        cases = self.cases()
        cases[0]['model'] = 'claude-mystery'
        e = batch_confirm.estimate(cases, 'medium')
        self.assertEqual([cases[0]['id']], e['unpriced'])


class ExactRequestTests(BatchCase):
    def test_only_requests_with_a_saved_body_are_sent_and_the_rest_are_counted_as_skipped(self):
        code, text = self.run_main('--kinds', 'reply,settings', submit=FakeSubmit())
        self.assertIn('reply: 1 exact request(s) at medium effort (1 without a saved body skipped)', text)
        self.assertIn('settings: 0 exact request(s) at medium effort (1 without a saved body skipped)', text)

    def test_count_caps_the_requests_per_kind(self):
        submit = FakeSubmit()
        self.run_main('--kinds', 'debrief', '--count', '2', '--approve', 'debrief', submit=submit)
        self.assertEqual(2, len(submit.sent[0]))

    def test_the_body_goes_as_saved_except_streaming_and_the_new_effort(self):
        submit = FakeSubmit()
        self.run_main('--kinds', 'debrief', '--approve', 'debrief', '--new-effort', 'low', submit=submit)
        for r in submit.sent[0]:
            params = r['params']
            self.assertNotIn('stream', params)
            self.assertEqual('claude-sonnet-5-5', params['model'])
            self.assertEqual(32000, params['max_tokens'])
            self.assertEqual({'type': 'object', 'x': 'saved'}, params['output_config']['format']['schema'])
            efforts = [m['output_config']['effort'] for m in params['messages'] if 'output_config' in m]
            self.assertEqual('low', efforts[-1])
            self.assertEqual('user', params['messages'][-1]['role'])

    def test_the_new_effort_replaces_only_this_turns_and_history_keeps_its_own(self):
        messages = [{'role': 'system', 'content': [], 'output_config': {'effort': 'high'}},
                    {'role': 'user', 'content': 'a'}, {'role': 'assistant', 'content': 'b'},
                    {'role': 'system', 'content': [], 'output_config': {'effort': 'high'}},
                    {'role': 'system', 'content': 'notice'}, {'role': 'user', 'content': 'c'}]
        out = batch_confirm.with_effort(messages, 'medium')
        self.assertEqual(['high', None, None, 'medium', None, None],
                         [m.get('output_config', {}).get('effort') for m in out])
        self.assertEqual(messages[:3], out[:3])
        self.assertEqual(messages[4:], out[4:])
        # No effort message this turn: one is added after the last assistant message.
        out = batch_confirm.with_effort(messages[:3] + messages[4:], 'medium')
        self.assertEqual('medium', out[3]['output_config']['effort'])
        self.assertEqual(len(messages), len(out))

    def test_the_input_body_is_not_changed(self):
        case = next(c for c in replay.check_rebuilds(replay.load_cases(self._tmp.name + '/m1')) if c['source'] == 'body')
        before = json.dumps(case['body'], sort_keys=True)
        batch_confirm.batch_request(case, 'low')
        self.assertEqual(before, json.dumps(case['body'], sort_keys=True))


class ResultTests(BatchCase):
    def test_all_agreeing_confirms_the_kind(self):
        _, text = self.run_main('--kinds', 'debrief', '--approve', 'debrief', submit=FakeSubmit())
        self.assertIn('3 of 3 agree with the originals; 0 missed a Blocker. CONFIRMED', text)

    def test_disagreements_are_printed_and_the_kind_is_not_confirmed(self):
        submit = FakeSubmit(lambda r: fx.debrief('Failed'))
        _, text = self.run_main('--kinds', 'debrief', '--approve', 'debrief', submit=submit)
        self.assertEqual(3, text.count('DISAGREES m1/turn-'))
        self.assertIn('"outcome": "Failed"', text)
        self.assertIn('0 of 3 agree with the originals', text)
        self.assertIn('NOT CONFIRMED', text)

    def test_a_missed_blocker_is_called_out(self):
        original = fx.findings('delta_v_short')
        case = dict(kind='craft_check', original_text=json.dumps(original))
        ok, missed, _, _ = batch_confirm.judge(case, json.dumps(fx.findings('no_landing_legs')))
        self.assertFalse(ok)
        self.assertEqual(['delta_v_short'], missed)

    def test_a_missing_reply_is_a_disagreement(self):
        ok, missed, sig, _ = batch_confirm.judge(dict(kind='debrief', original_text=json.dumps(fx.debrief('Nominal'))), '')
        self.assertFalse(ok)
        self.assertIsNone(sig)


if __name__ == '__main__':
    unittest.main()
