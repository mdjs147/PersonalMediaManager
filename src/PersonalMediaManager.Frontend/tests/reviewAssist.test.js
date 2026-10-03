import assert from 'node:assert/strict';
import test from 'node:test';
import { createReviewAssist, createReviewSubmission } from '../src/composables/useReviewAssist.js';
import { defaultEpisodeOptions } from '../src/utils/reviewEpisodePlan.js';

function deferred() { let resolve; let reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function snapshot() { return { rows: [{ id: 1, fileName: 'Show.1.mkv', rowVersion: 1 }, { id: 2, fileName: 'Show.2.mkv', rowVersion: 1 }], edits: { 1: { checked: true, season: null, episode: null, episodeEnd: null }, 2: { checked: true, season: null, episode: null, episodeEnd: null } }, options: defaultEpisodeOptions(), identity: 'Tv:7' }; }
const hints = { items: [{ id: 1, season: 1, episode: 1, evidence: 'S01E01' }, { id: 2, error: '无明确集号' }] };

test('库内候选按来源顺序保留且从不自动选择，不同媒体类型同ID不混淆', async () => {
  const assist = createReviewAssist({ libraryCandidates: async () => ({ items: [{ tmdbId: 7, mediaType: 'tv', source: 'LibraryContext' }, { tmdbId: 7, mediaType: 'movie', source: 'LibraryTitle' }, { tmdbId: 8, mediaType: 'tv', source: 'LibraryRecent' }, { tmdbId: 9 }] }) });
  assert.equal(await assist.loadLibrary(1), true);
  assert.deepEqual(assist.libraryCandidates.value.map((item) => [item.mediaType, item.source]), [['Tv', 'LibraryContext'], ['Movie', 'LibraryTitle'], ['Tv', 'LibraryRecent']]);
  assert.equal('selected' in assist, false);
});

test('换组、关闭和后发搜索使旧库内响应失效', async () => {
  const one = deferred(); const two = deferred(); let count = 0;
  const assist = createReviewAssist({ libraryCandidates: () => (++count === 1 ? one.promise : two.promise) });
  const request1 = assist.loadLibrary(1); const request2 = assist.loadLibrary(2);
  two.resolve({ items: [{ tmdbId: 2, mediaType: 'tv' }] }); await request2;
  one.resolve({ items: [{ tmdbId: 1, mediaType: 'tv' }] }); await request1;
  assert.equal(assist.libraryCandidates.value[0].tmdbId, 2);
  assist.reset(); assert.deepEqual(assist.libraryCandidates.value, []);
});

test('库内加载失败可重试；失败不污染候选选择', async () => {
  let fail = true; const assist = createReviewAssist({ libraryCandidates: async () => { if (fail) throw new Error('离线'); return { items: [] }; } });
  assert.equal(await assist.loadLibrary(1), false); assert.match(assist.libraryError.value, /离线/);
  fail = false; assert.equal(await assist.loadLibrary(1), true); assert.equal(assist.libraryError.value, '');
});

test('提取必须先预览再显式应用，仅应用有证据项，原表单保持不动', async () => {
  const assist = createReviewAssist({ episodeHints: async () => hints }); const current = snapshot();
  await assist.prepare(current); assert.equal(current.edits[1].episode, null); assert.equal(assist.applicableCount.value, 1);
  const result = assist.apply(current);
  assert.equal(result.count, 1); assert.equal(result.edits[1].episode, 1); assert.equal(result.edits[1].decisionSource, 'RuleExtraction'); assert.equal(result.edits[2].episode, null);
  assert.equal(assist.preview.value, null); assert.equal(assist.apply(current), null);
});

test('连续点击提取只发一次，取消后晚响应不回填', async () => {
  const pending = deferred(); let calls = 0; let signal;
  const assist = createReviewAssist({ episodeHints: (_, options) => { calls += 1; signal = options.signal; return pending.promise; } });
  const request = assist.prepare(snapshot()); assert.equal(await assist.prepare(snapshot()), false); assert.equal(calls, 1);
  assist.cancelPreview(); assert.equal(signal.aborted, true); pending.resolve(hints); await request;
  assert.equal(assist.preview.value, null); assert.equal(assist.preparing.value, false);
});

test('请求中换组或编辑均不覆盖新内容，过期计划不能应用', async () => {
  const pending = deferred(); const assist = createReviewAssist({ episodeHints: () => pending.promise }); const current = snapshot();
  const request = assist.prepare(current); current.edits[1].episode = 6;
  pending.resolve(hints); await request; assert.equal(assist.isStale(current), true); assert.equal(assist.apply(current), null); assert.match(assist.error.value, /已变化/);
  assist.reset(); assert.equal(assist.preview.value, null);
});

test('请求失败、空选中或重复响应项不形成可应用计划', async () => {
  const assist = createReviewAssist({ episodeHints: async () => { throw new Error('超时'); } });
  await assist.prepare(snapshot()); assert.match(assist.error.value, /超时/); assert.equal(assist.preview.value, null);
  const empty = snapshot(); empty.edits[1].checked = false; empty.edits[2].checked = false;
  assert.equal(await assist.prepare(empty), false); assert.match(assist.error.value, /勾选/);
  const duplicated = createReviewAssist({ episodeHints: async () => ({ items: [{ id: 1 }, { id: 1 }] }) });
  assert.equal(await duplicated.prepare(snapshot()), false); assert.match(duplicated.error.value, /重复/);
});

test('批填应用前选项或范围变化必须重新预览', async () => {
  const assist = createReviewAssist({}); const current = snapshot(); current.options = { ...current.options, mode: 'sequence', season: 1 };
  await assist.prepare(current); current.options.start = 3;
  assert.equal(assist.apply(current), null);
  await assist.prepare(current); current.edits[2].checked = false; assert.equal(assist.apply(current), null);
});

test('绝对映射必须显式确认且目标季至少2，应用保留服务端令牌与源编号', async () => {
  let calls = 0; const assist = createReviewAssist({ episodeMappingPreview: async () => { calls += 1; return { items: [{ id: 1, season: 2, episode: 1, mappingToken: 'opaque' }, { id: 2, error: '明确SxxEyy不能换算' }] }; } });
  const current = snapshot(); current.edits[1].episode = 13; current.edits[2].episode = 14;
  assert.equal(await assist.prepare(current, { acknowledged: false, season: 2 }), false);
  assert.equal(await assist.prepare(current, { acknowledged: true, season: 1 }), false); assert.equal(calls, 0);
  await assist.prepare(current, { acknowledged: true, tmdbId: 7, season: 2 });
  const result = assist.apply(current); assert.equal(result.edits[1].mappingToken, 'opaque'); assert.equal(result.edits[1].sourceEpisode, 13); assert.equal(result.edits[1].decisionSource, 'AbsoluteMapping'); assert.equal(result.edits[2].episode, 14);
});

test('映射没有令牌或返回重复落点时不能应用', async () => {
  for (const mappingToken of [null, 'opaque']) {
    const assist = createReviewAssist({ episodeMappingPreview: async () => ({ items: [{ id: 1, season: 2, episode: 1, mappingToken }, { id: 2, season: 2, episode: 1, mappingToken }] }) });
    const current = snapshot(); current.edits[1].episode = 13; current.edits[2].episode = 14;
    await assist.prepare(current, { acknowledged: true, season: 2 });
    assert.equal(assist.applicableCount.value, 0); assert.equal(assist.apply(snapshot()), null);
  }
});

test('确认在提示前上锁，重复点击不重复发请求，取消不提交', async () => {
  const pending = deferred(); let calls = 0; const submission = createReviewSubmission({ batchConfirm: async () => { calls += 1; return { succeeded: [1] }; } });
  const first = submission.submit([{ id: 1 }], () => pending.promise);
  assert.equal(submission.busy.value, true); assert.equal(await submission.submit([{ id: 1 }]), null);
  pending.resolve(false); assert.equal(await first, null); assert.equal(calls, 0); assert.equal(submission.busy.value, false);
});

test('确认分片串行返回逐项失败，成功条目可从页面移除', async () => {
  let active = 0; let maxActive = 0;
  const submission = createReviewSubmission({ batchConfirm: async ({ items }) => { active += 1; maxActive = Math.max(active, maxActive); await Promise.resolve(); active -= 1; return { succeeded: items.filter((x) => x.id !== 3).map((x) => x.id), failed: items.filter((x) => x.id === 3).map((x) => ({ id: x.id, message: '版本冲突' })) }; } }, 2);
  const result = await submission.submit([1, 2, 3, 4, 5].map((id) => ({ id })));
  assert.deepEqual(result.succeeded, [1, 2, 4, 5]); assert.deepEqual(result.failed, [{ id: 3, message: '版本冲突' }]); assert.equal(maxActive, 1); assert.equal(submission.progress.value.visible, false);
});

test('确认传输中断停止后续分片并标记不确定，不自动重试', async () => {
  let calls = 0; const submission = createReviewSubmission({ batchConfirm: async ({ items }) => { calls += 1; if (calls === 2) throw new Error('网络中断'); return { succeeded: items.map((i) => i.id) }; } }, 1);
  const result = await submission.submit([1, 2, 3].map((id) => ({ id })));
  assert.deepEqual(result.succeeded, [1]); assert.equal(result.uncertain, true); assert.deepEqual(result.failed.map((item) => item.id), [2, 3]); assert.equal(calls, 2);
});

test('元数据刷新告警和确认失败分别返回，成功仍保持成功', async () => {
  const submission = createReviewSubmission({ batchConfirm: async () => ({ succeeded: [1], failed: [], metadataWarnings: [{ id: 1, message: 'TMDB离线' }] }) });
  const result = await submission.submit([{ id: 1 }]);
  assert.deepEqual(result.succeeded, [1]); assert.deepEqual(result.failed, []); assert.equal(result.metadataWarnings[0].message, 'TMDB离线');
});

test('响应缺项或成功失败互相矛盾时停止后续提交并要求刷新', async () => {
  for (const response of [{ succeeded: [], failed: [] }, { succeeded: [1], failed: [{ id: 1, message: '矛盾' }] }]) {
    let calls = 0; const submission = createReviewSubmission({ batchConfirm: async () => { calls += 1; return response; } }, 1);
    const result = await submission.submit([{ id: 1 }, { id: 2 }]);
    assert.equal(result.uncertain, true); assert.equal(calls, 1); assert.deepEqual(result.succeeded, []); assert.equal(result.failed.length, 2);
  }
});
