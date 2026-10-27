"""Tests for the per-plan figures in baseline.py (#286). The log lines are hand-written in the shape the mod writes."""
import json
import os
import tempfile
import unittest

import baseline


def plan_line(n, name='Hop', **over):
    d = {'event': 'plan', 'plan': n, 'name': name, 'purpose': 'SequencePlan', 'manual': False,
         'tools': {}, 'tool_calls': 0, 'blocks': {'circularize': 1}, 'custom_script': False, 'custom_lines': 0,
         'lints': [], 'effort': 'medium', 'requests': 1, 'output_tokens': 900, 'thinking_tokens': 600,
         'reply_seconds': 12.5, 'cost': 0.05, 'second_look': None, 'cut_off_retry': False, 'script_rewrite': False,
         'at': '2026-10-26T00:00:00.0000000Z'}
    d.update(over)
    return d


def debriefed(line, outcome, cost=0.04, second_look=None):
    line = dict(line)
    line['debrief'] = {'outcome': outcome, 'effort': 'medium', 'requests': 1, 'output_tokens': 300, 'thinking_tokens': 100,
                       'reply_seconds': 8.0, 'cost': cost, 'second_look': second_look, 'cut_off_retry': False}
    return line


class LoadPlansCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)

    def write(self, text):
        with open(os.path.join(self._tmp.name, 'plans.jsonl'), 'w', encoding='utf-8') as f:
            f.write(text)

    def test_reads_one_dict_per_line_and_skips_what_is_not_a_plan_line(self):
        self.write(json.dumps(plan_line(1)) + '\n' + 'not json\n' + '\n' + json.dumps([1]) + '\n'
                   + json.dumps({'event': 'other'}) + '\n' + json.dumps(plan_line(2)) + '\n')
        self.assertEqual([1, 2], [p['plan'] for p in baseline.load_plans(self._tmp.name)])

    def test_a_mission_with_no_log_has_no_plans(self):
        self.assertEqual([], baseline.load_plans(self._tmp.name))


class ReportCase(unittest.TestCase):
    def report(self, plans):
        return '\n'.join(baseline.plans_report(plans))

    def test_no_plans_no_section(self):
        self.assertEqual([], baseline.plans_report([]))

    def test_each_plan_gets_a_row_with_its_figures_and_its_debrief_outcome(self):
        text = self.report([
            ('m1', debriefed(plan_line(1, tools={'predict_burn': 2, 'stage_performance': 1}, tool_calls=3, cost=0.123456),
                             'Off-Nominal', second_look='fuel figure stale')),
            ('m1', plan_line(2, name='Land', custom_script=True, custom_lines=14, lints=['line 3: a loop'], cut_off_retry=True,
                             second_look='x', blocks={})),
        ])
        rows = [l for l in text.split('\n') if l.startswith('| m1 ')]
        self.assertEqual(2, len(rows))
        self.assertIn('predict_burn x2, stage_performance', rows[0])
        self.assertIn('$0.1235', rows[0])
        self.assertIn('Off-Nominal', rows[0])
        self.assertIn('$0.0400', rows[0])
        self.assertIn('12.5 s', rows[0])
        self.assertIn('yes (14)', rows[1])
        self.assertIn('not yet', rows[1])

    def test_groups_split_custom_scripts_from_building_blocks_and_count_what_went_wrong(self):
        text = self.report([
            ('m1', debriefed(plan_line(1, cost=0.10), 'Nominal', cost=0.02)),
            ('m1', debriefed(plan_line(2, cost=0.30, custom_script=True, custom_lines=9, lints=['l']), 'Failed', cost=0.06)),
            ('m2', plan_line(1, cost=0.20, custom_script=True, cut_off_retry=True)),
        ])
        rows = {}
        for l in text.split('\n'):
            cells = [c.strip() for c in l.split('|')]
            if len(cells) > 3 and cells[1] in ('All', 'Custom Script', 'Building Blocks only', 'With lints'):
                rows[cells[1]] = l
        self.assertEqual(['All', 'Building Blocks only', 'Custom Script', 'With lints'], sorted(rows))
        self.assertIn('| 3 | $0.6000 | $0.2000 |', rows['All'])
        self.assertIn('| 2 | $0.5000 | $0.2500 |', rows['Custom Script'])
        self.assertIn('1 of 1 Off-Nominal or Failed; $0.3600 a debriefed plan', rows['Custom Script'])
        self.assertIn('0 of 1 Off-Nominal or Failed', rows['Building Blocks only'])
        self.assertIn('| 1 | $0.3000 |', rows['With lints'])

    def test_tool_calls_are_totalled_across_plans(self):
        text = self.report([('m1', plan_line(1, tools={'predict_burn': 2})), ('m2', plan_line(1, tools={'predict_burn': 1, 'slope': 4}))])
        self.assertIn('Planning Tool calls: predict_burn 3, slope 4', text)

    def test_text_from_the_log_cannot_break_the_table(self):
        text = self.report([('m|1', plan_line(1, name='A|B\nC'))])
        row = [l for l in text.split('\n') if l.startswith('| m 1 ')]
        self.assertEqual(1, len(row))
        self.assertIn('A B C', row[0])


class MainCase(unittest.TestCase):
    def test_the_report_reads_plans_jsonl_from_every_mission_folder(self):
        with tempfile.TemporaryDirectory() as saves:
            folder = os.path.join(saves, 's1', 'ClaudiaKermin', 'missions', 'm9')
            os.makedirs(folder)
            with open(os.path.join(folder, 'plans.jsonl'), 'w', encoding='utf-8') as f:
                f.write(json.dumps(debriefed(plan_line(1, name='Hop to the Mun'), 'Nominal')) + '\n')
            out = os.path.join(saves, 'report.md')
            old = baseline.sys.argv
            baseline.sys.argv = ['baseline.py', saves, '--out', out]
            try:
                baseline.main()
            finally:
                baseline.sys.argv = old
            with open(out, encoding='utf-8') as f:
                text = f.read()
        self.assertIn('## Per Sequence Plan', text)
        self.assertIn('| m9 | 1 | Hop to the Mun |', text)
        self.assertIn('Nominal', text)


if __name__ == '__main__':
    unittest.main()
