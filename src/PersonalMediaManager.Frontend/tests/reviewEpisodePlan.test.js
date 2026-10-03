import assert from 'node:assert/strict';
import test from 'node:test';
import { buildEpisodePlan, defaultEpisodeOptions, episodePlanFingerprint, episodeProblems, naturalFileOrder } from '../src/utils/reviewEpisodePlan.js';

const row = (id, fileName = `Show.${id}.mkv`, title = '示例剧') => ({ id, fileName, rowVersion: 1, parsedInfo: JSON.stringify({ title }) });
const edit = (season = null, episode = null, episodeEnd = null, checked = true) => ({ season, episode, episodeEnd, checked });
const sequence = (overrides = {}) => ({ ...defaultEpisodeOptions(), mode: 'sequence', season: 1, ...overrides });

test('批填按自然文件顺序，固定起点步长，只影响勾选范围', () => {
  const rows = [row(10), row(2), row(1), row(4)];
  const edits = { 10: edit(), 2: edit(), 1: edit(), 4: edit(null, null, null, false) };
  const plan = buildEpisodePlan(rows, edits, sequence({ start: 5, step: 2 }));
  assert.deepEqual(plan.map((e) => [e.id, e.after.episode]), [[1, 5], [2, 7], [10, 9]]);
  assert.deepEqual(naturalFileOrder(rows).map((e) => e.id), [1, 2, 4, 10]);
  assert.equal(edits[1].episode, null);
});

test('保留已有字段默认开启，已有行仍占排序编号位置', () => {
  const rows = [row(1), row(2), row(3)];
  const edits = { 1: edit(1, 1), 2: edit(), 3: edit(1, 3) };
  const plan = buildEpisodePlan(rows, edits, sequence());
  assert.deepEqual(plan.map((e) => e.after.episode), [1, 2, 3]);
  assert.deepEqual(plan.map((e) => e.changed), [false, true, false]);
  const overwrite = buildEpisodePlan(rows, edits, sequence({ start: 4, keepExisting: false }));
  assert.deepEqual(overwrite.map((e) => e.after.episode), [4, 5, 6]);
});

test('未填季号时只填集号，不暗设第一季；可明确选择特别篇季零', () => {
  assert.equal(buildEpisodePlan([row(1)], { 1: edit() }, sequence({ season: null }))[0].after.season, null);
  assert.equal(buildEpisodePlan([row(1)], { 1: edit() }, sequence({ season: 0 }))[0].after.season, 0);
});

test('同剧跨季、多个标题、特别篇和合并区间不得静默重编号', () => {
  const cases = [
    [[row(1), row(2)], { 1: edit(1, 1), 2: edit(2, 1) }, '跨季'],
    [[row(1), row(2, 'Other.2.mkv', '另一部')], { 1: edit(), 2: edit() }, '多个解析标题'],
    [[row(1)], { 1: edit(0, 1) }, '特别篇'],
    [[row(1)], { 1: edit(1, 1, 2) }, '合并多集'],
  ];
  for (const [rows, edits, expected] of cases) {
    const plan = buildEpisodePlan(rows, edits, sequence({ keepExisting: false }));
    assert.ok(plan.some((entry) => entry.error.includes(expected)), expected);
    assert.ok(plan.every((entry) => !entry.changed));
  }
});

test('重复集和区间重叠逐项标错，包含保留值', () => {
  const rows = [row(1), row(2)];
  const edits = { 1: edit(1, 2), 2: edit(1) };
  const plan = buildEpisodePlan(rows, edits, sequence());
  assert.ok(plan.every((e) => e.error.includes('重复')));
  assert.equal(Object.keys(episodeProblems(rows, { 1: edit(1, 1, 3), 2: edit(1, 3) })).length, 2);
});

test('规则提取逐项保留证据和失败，无证据不猜值', () => {
  const rows = [row(1), row(2), row(3), row(4)];
  const edits = Object.fromEntries(rows.map((r) => [r.id, edit()]));
  edits[1] = edit(2);
  const plan = buildEpisodePlan(rows, edits, defaultEpisodeOptions(), [
    { id: 1, season: 1, episode: 3, evidence: 'S01E03' }, { id: 2, error: '规则冲突' }, { id: 3 },
  ]);
  assert.deepEqual(plan[0].after, { season: 2, episode: 3, episodeEnd: null });
  assert.equal(plan[0].evidence, 'S01E03');
  assert.match(plan[0].warnings[0], /保留已有季号/);
  assert.equal(plan[1].error, '规则冲突');
  assert.match(plan[2].error, /没有可用/);
  assert.match(plan[3].error, /未返回/);
});

test('无效数值、集号溢出、倒置末集无法应用', () => {
  for (const options of [sequence({ start: -1 }), sequence({ step: 0 }), sequence({ season: 1.5 }), sequence({ start: 9999, step: 1 })]) {
    const plan = buildEpisodePlan([row(1), row(2)], { 1: edit(), 2: edit() }, options);
    assert.ok(plan.some((entry) => entry.error));
  }
  assert.ok(episodeProblems([row(1)], { 1: edit(1, 4, 3) })[1]);
});

test('预览指纹覆盖作品、选中范围、行版本、表单与选项', () => {
  const rows = [row(1)]; const edits = { 1: edit() }; const options = sequence();
  const first = episodePlanFingerprint(rows, edits, options, 'Tv:1');
  assert.notEqual(first, episodePlanFingerprint(rows, edits, options, 'Tv:2'));
  assert.notEqual(first, episodePlanFingerprint(rows, { 1: edit(2) }, options, 'Tv:1'));
  assert.notEqual(first, episodePlanFingerprint(rows, { 1: edit(null, null, null, false) }, options, 'Tv:1'));
  assert.notEqual(first, episodePlanFingerprint([{ ...rows[0], rowVersion: 2 }], edits, options, 'Tv:1'));
  assert.notEqual(first, episodePlanFingerprint(rows, edits, { ...options, step: 2 }, 'Tv:1'));
});
