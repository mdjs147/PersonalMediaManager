import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('replay', Path(__file__).parents[1] / 'parse-diagnostics-replay.py')
replay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(replay)

class ReplayTests(unittest.TestCase):
    def document(self, **changes):
        field = {'state': 'recorded', 'text': 'Show.S01E02.mkv', 'truncated': False, 'redacted': False}
        field.update(changes)
        return {'events': [{'runId': 'a', 'name': 'parse.input', 'data': {'fileName': field, 'relativeSegments': []}}]}

    def test_complete_input_is_reconstructed_without_truth_claim(self):
        result = replay.reconstruct(self.document())
        self.assertEqual(result['fixtures'][0]['fileName'], 'Show.S01E02.mkv')
        self.assertFalse(result['fixtures'][0]['groundTruth'])
        self.assertFalse(result['completePipelineReplay'])

    def test_incomplete_states_are_rejected(self):
        for changes in [{'truncated': True}, {'redacted': True}, {'state': 'unknown'}, {'state': 'missing'}, {'state': 'not_recorded'}]:
            result = replay.reconstruct(self.document(**changes))
            self.assertEqual(result['fixtures'], [])
            self.assertEqual(len(result['rejected']), 1)

    def test_manual_changes_in_separate_run_remain_linked_by_media(self):
        document = self.document()
        document['events'][0]['mediaItemId'] = 42
        document['events'].append({'runId': 'manual', 'mediaItemId': 42, 'name': 'manual.change_committed',
                                   'data': {'groundTruth': False, 'source': 'ReviewApi', 'before': 1, 'after': 2}})
        result = replay.reconstruct(document)
        self.assertEqual(len(result['manualChanges']), 1)
        self.assertEqual(result['fixtures'][0]['manualChanges'][0]['runId'], 'manual')
        self.assertFalse(result['fixtures'][0]['groundTruth'])

    def test_missing_input_is_distinct(self):
        result = replay.reconstruct({'events': [{'runId': 'a', 'name': 'rule.result', 'data': {}}]})
        self.assertEqual(result['rejected'][0]['reason'], 'input_event_missing')

if __name__ == '__main__':
    unittest.main()
