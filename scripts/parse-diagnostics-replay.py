#!/usr/bin/env python3
"""从本地诊断导出包提取可验证的规则输入；不联网、不调用模型、不自动当作真值。"""
import argparse
import json
import os
from pathlib import Path


def captured(value):
    if not isinstance(value, dict) or value.get('state') != 'recorded':
        raise ValueError('input_not_recorded')
    if value.get('truncated') or value.get('redacted'):
        raise ValueError('input_redacted_or_truncated')
    if not isinstance(value.get('text'), str):
        raise ValueError('input_text_missing')
    return value['text']


def reconstruct(document):
    data = document.get('data', document)
    events = data.get('events', [])
    manual_changes = [event for event in events if event.get('name') == 'manual.change_committed']
    runs = {}
    for event in events:
        runs.setdefault(event['runId'], []).append(event)
    fixtures, rejected = [], []
    for run, trace in runs.items():
        inputs = [event for event in trace if event['name'] == 'parse.input']
        if not inputs:
            rejected.append({'runId': run, 'reason': 'input_event_missing'})
            continue
        try:
            if len(inputs) != 1 or inputs[0].get('dataRedacted'):
                raise ValueError('input_ambiguous_or_redacted')
            value = inputs[0]['data']
            file_name = captured(value.get('fileName'))
            segments = [captured(v) for v in value['relativeSegments']]
            fixtures.append({'runId': run, 'mediaItemId': inputs[0].get('mediaItemId'),
                             'scanRunId': inputs[0].get('scanRunId'), 'fileName': file_name,
                             'relativeSegments': segments,
                             'recordedRuleResults': [e['data'] for e in trace if e['name'] == 'rule.result'],
                             'manualChanges': [e for e in manual_changes if e.get('mediaItemId') == inputs[0].get('mediaItemId')],
                             'groundTruth': False,
                             'pipelineEnded': any(e['name'] == 'parse.finished' for e in trace)})
        except (ValueError, KeyError, TypeError) as error:
            rejected.append({'runId': run, 'reason': str(error)})
    return {'schemaVersion': 1, 'purpose': 'rule_input_reconstruction',
            'completePipelineReplay': False, 'fixtures': fixtures, 'rejected': rejected,
            'manualChanges': manual_changes,
            'limitations': ['不会恢复被脱敏、截断、未记录或已过期内容',
                           '记录结果与人工修正不是自动真值；重新运行规则需原规则配置和同一构建',
                           '不重放真实AI或TMDB调用，不上传任何内容']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('export', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    if args.export.resolve() == args.output.resolve():
        parser.error('输入与输出不能相同')
    if args.export.stat().st_size > 32 * 1024 * 1024:
        parser.error('导出包超过32MiB读取上限')
    result = reconstruct(json.loads(args.export.read_text(encoding='utf-8')))
    with os.fdopen(os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'w', encoding='utf-8') as file:
        json.dump(result, file, ensure_ascii=False, indent=2)
    print(f"已重建{len(result['fixtures'])}条规则输入；{len(result['rejected'])}条因缺口未重建")


if __name__ == '__main__':
    main()
