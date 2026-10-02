import test from 'node:test';
import assert from 'node:assert/strict';
import { findTmdbCandidate, mergeTmdbCandidates, normalizeTmdbMediaType, sameTmdbCandidate,
  tmdbCandidateIdentity, tmdbCandidateKey, upsertTmdbCandidate } from '../src/utils/tmdbCandidateIdentity.js';

test('同数值电影和剧集身份保留为两个候选', () => {
  const values = mergeTmdbCandidates([{ tmdbId: 123, mediaType: 'tv', title: '剧集' },
    { tmdbId: 123, mediaType: 'movie', title: '电影' }]);
  assert.equal(values.length, 2);
  assert.equal(tmdbCandidateKey(values[0]), 'Tv:123');
  assert.equal(tmdbCandidateKey(values[1]), 'Movie:123');
});

test('类型大小写归一但未知类型不猜电影', () => {
  assert.equal(normalizeTmdbMediaType(' TV '), 'Tv');
  assert.equal(normalizeTmdbMediaType('Movie'), 'Movie');
  for (const value of [null, '', 'unknown', 'both', 'film']) assert.equal(normalizeTmdbMediaType(value), null);
});

test('坏ID和缺少类型的旧候选不能成为可选身份', () => {
  for (const tmdbId of [0, -1, 1.5, NaN, Infinity, '1e3', 'invalid'])
    assert.equal(tmdbCandidateIdentity({ tmdbId, mediaType: 'movie' }), null);
  assert.equal(tmdbCandidateIdentity({ tmdbId: 1 }), null);
  assert.equal(mergeTmdbCandidates([{ tmdbId: 1 }, { tmdbId: 1, mediaType: 'unknown' }]).length, 0);
});

test('同型ID重复不会按别名数量增加作品数', () => {
  const values = mergeTmdbCandidates([{ tmdbId: 12, mediaType: 'Tv', title: '中文' },
    { tmdbId: '12', mediaType: 'tv', title: 'English' }]);
  assert.equal(values.length, 1);
  assert.equal(values[0].title, '中文');
});

test('选中身份和高亮同时比较类型与ID', () => {
  const tv = { tmdbId: 123, mediaType: 'Tv' };
  const movie = { tmdbId: 123, mediaType: 'Movie' };
  assert.equal(sameTmdbCandidate(tv, movie), false);
  assert.equal(sameTmdbCandidate(tv, { tmdbId: '123', mediaType: 'tv' }), true);
  assert.equal(findTmdbCandidate([tv, movie], movie), movie);
  assert.equal(findTmdbCandidate([tv], movie), null);
});

test('手动搜索同号电影不覆盖原剧集或其季目录', () => {
  const tv = { tmdbId: 123, mediaType: 'Tv', title: '原剧集', totalSeasons: 4 };
  const values = [tv];
  const movie = upsertTmdbCandidate(values, { tmdbId: 123, mediaType: 'movie', title: '新电影' });
  assert.equal(values.length, 2);
  assert.equal(tv.title, '原剧集');
  assert.equal(tv.totalSeasons, 4);
  assert.equal(movie.mediaType, 'Movie');
  assert.equal(movie.totalSeasons, undefined);
});

test('同型搜索补全可用字段而不以空值清除已知字段', () => {
  const original = { tmdbId: 1, mediaType: 'Tv', title: '旧名', totalSeasons: 2 };
  const values = [original];
  const updated = upsertTmdbCandidate(values, { tmdbId: '1', mediaType: 'tv', title: '新名', totalSeasons: null });
  assert.equal(updated, original);
  assert.equal(values.length, 1);
  assert.equal(updated.title, '新名');
  assert.equal(updated.totalSeasons, 2);
});

test('未知类型搜索结果不能覆盖列表或制造默认选中电影', () => {
  const values = [{ tmdbId: 123, mediaType: 'Tv', title: '原剧集' }];
  assert.equal(upsertTmdbCandidate(values, { tmdbId: 123, mediaType: 'unknown', title: '错误' }), null);
  assert.equal(values.length, 1);
  assert.equal(values[0].title, '原剧集');
  assert.equal(sameTmdbCandidate({}, {}), false);
});
