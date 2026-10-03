import { computed, ref } from 'vue';
import { mergeTmdbCandidates } from '../utils/tmdbCandidateIdentity.js';
import { buildEpisodePlan, episodePlanFingerprint, episodeProblems } from '../utils/reviewEpisodePlan.js';

const copy = (value) => JSON.parse(JSON.stringify(value));
const message = (error) => error?.message || '请求失败，请重试';

/** 只读辅助与表单应用分开；取消、换组和新请求均使旧响应失效。 */
export function createReviewAssist(api) {
  const libraryCandidates = ref([]);
  const libraryLoading = ref(false);
  const libraryError = ref('');
  const preview = ref(null);
  const preparing = ref(false);
  const error = ref('');
  let generation = 0;
  let libraryRequest = 0;
  let planRequest = 0;
  let libraryAbort;
  let planAbort;
  const applicableCount = computed(() => preview.value?.entries.filter((entry) => entry.changed && !entry.error).length || 0);

  function cancelPreview() {
    planRequest += 1;
    planAbort?.abort();
    preparing.value = false;
    preview.value = null;
    error.value = '';
  }
  function reset() {
    generation += 1;
    libraryRequest += 1;
    libraryAbort?.abort();
    libraryCandidates.value = [];
    libraryLoading.value = false;
    libraryError.value = '';
    cancelPreview();
  }
  async function loadLibrary(id, query = '') {
    const context = generation;
    const request = ++libraryRequest;
    libraryAbort?.abort();
    libraryAbort = new AbortController();
    libraryLoading.value = true;
    libraryError.value = '';
    try {
      const result = await api.libraryCandidates(id, { query: query.trim() || undefined }, { signal: libraryAbort.signal });
      if (context !== generation || request !== libraryRequest) return false;
      libraryCandidates.value = mergeTmdbCandidates(result?.items || []);
      return true;
    } catch (cause) {
      if (context === generation && request === libraryRequest && cause?.name !== 'AbortError') libraryError.value = `库内候选加载失败：${message(cause)}`;
      return false;
    } finally {
      if (context === generation && request === libraryRequest) libraryLoading.value = false;
    }
  }
  async function prepare(snapshot, mapping = null) {
    if (preparing.value) return false;
    const frozen = copy(snapshot);
    if (!frozen.rows.some((row) => frozen.edits[row.id]?.checked)) { error.value = '请先勾选文件'; return false; }
    const context = generation;
    const request = ++planRequest;
    planAbort = new AbortController();
    preparing.value = true;
    preview.value = null;
    error.value = '';
    try {
      let entries;
      let response;
      const rows = frozen.rows.filter((row) => frozen.edits[row.id]?.checked);
      if (mapping) {
        if (!mapping.acknowledged) throw new Error('请先确认这些集号是跨季累计编号');
        if (!Number.isInteger(mapping.season) || mapping.season < 2 || mapping.season > 100) throw new Error('请选择第 2 季或以后的目标季');
        const missing = rows.find((row) => !Number.isInteger(frozen.edits[row.id]?.episode) || frozen.edits[row.id].episode < 1);
        if (missing) throw new Error(`${missing.fileName}：请先提取或填写大于零的原始累计集号`);
        response = await api.episodeMappingPreview({
          tmdbId: mapping.tmdbId, mediaType: 'tv', season: mapping.season,
          forceRefresh: !!mapping.forceRefresh,
          items: rows.map((row) => ({ id: row.id, rowVersion: row.rowVersion, episode: frozen.edits[row.id]?.episode, episodeEnd: frozen.edits[row.id]?.episodeEnd ?? null })),
        }, { signal: planAbort.signal });
        const returned = response?.items || [];
        const seen = new Set();
        for (const item of returned) {
          if (seen.has(item.id)) throw new Error('后端返回重复文件结果，未应用');
          seen.add(item.id);
        }
        const byId = new Map(returned.map((item) => [item.id, item]));
        entries = rows.map((row) => {
          const hint = byId.get(row.id);
          const before = Object.fromEntries(['season', 'episode', 'episodeEnd'].map((key) => [key, frozen.edits[row.id]?.[key] ?? null]));
          const after = hint && !hint.error ? { season: hint.season, episode: hint.episode, episodeEnd: hint.episodeEnd ?? null } : { ...before };
          return { id: row.id, fileName: row.fileName, before, after, source: 'AbsoluteMapping', evidence: hint?.evidence, mappingToken: hint?.mappingToken, sourceEpisode: before.episode, sourceEpisodeEnd: before.episodeEnd, error: hint?.error || (!hint ? '缺少该文件的换算结果' : ''), warnings: [], changed: JSON.stringify(before) !== JSON.stringify(after) };
        });
        const planned = { ...frozen.edits };
        for (const entry of entries) planned[entry.id] = { ...frozen.edits[entry.id], ...entry.after };
        const problems = episodeProblems(frozen.rows, planned);
        for (const entry of entries) { entry.error ||= problems[entry.id] || (!entry.mappingToken ? '缺少映射校验令牌，请重新预览' : ''); entry.changed &&= !entry.error; }
      } else {
        let hints = [];
        if (frozen.options.mode === 'rules') {
          response = await api.episodeHints({ items: rows.map((row) => ({ id: row.id, rowVersion: row.rowVersion })) }, { signal: planAbort.signal });
          hints = response?.items || [];
          if (new Set(hints.map((item) => item.id)).size !== hints.length) throw new Error('后端返回重复文件结果，未应用');
        }
        entries = buildEpisodePlan(frozen.rows, frozen.edits, frozen.options, hints);
      }
      if (context !== generation || request !== planRequest) return false;
      preview.value = { entries, fingerprint: episodePlanFingerprint(frozen.rows, frozen.edits, frozen.options, frozen.identity), cachedAt: response?.cachedAt, refreshError: response?.refreshError, mode: mapping ? 'mapping' : frozen.options.mode };
      return true;
    } catch (cause) {
      if (context === generation && request === planRequest && cause?.name !== 'AbortError') error.value = message(cause);
      return false;
    } finally {
      if (context === generation && request === planRequest) preparing.value = false;
    }
  }
  function isStale(snapshot) {
    return !!preview.value && preview.value.fingerprint !== episodePlanFingerprint(snapshot.rows, snapshot.edits, snapshot.options, snapshot.identity);
  }
  function apply(snapshot) {
    if (!preview.value || preparing.value) return null;
    if (isStale(snapshot)) { error.value = '选中范围、作品或字段已变化，请重新预览'; return null; }
    const edits = copy(snapshot.edits);
    let count = 0;
    for (const entry of preview.value.entries) {
      if (!entry.changed || entry.error) continue;
      delete edits[entry.id].mappingToken;
      delete edits[entry.id].sourceEpisode;
      delete edits[entry.id].sourceEpisodeEnd;
      edits[entry.id] = { ...edits[entry.id], ...entry.after, decisionSource: entry.source === 'AbsoluteMapping' ? 'AbsoluteMapping' : preview.value.mode === 'rules' ? 'RuleExtraction' : 'BatchFill' };
      if (entry.source === 'AbsoluteMapping') Object.assign(edits[entry.id], { mappingToken: entry.mappingToken, sourceEpisode: entry.sourceEpisode, sourceEpisodeEnd: entry.sourceEpisodeEnd });
      count += 1;
    }
    if (!count) return null;
    cancelPreview();
    return { edits, count };
  }
  return { libraryCandidates, libraryLoading, libraryError, preview, preparing, error, applicableCount, reset, loadLibrary, cancelPreview, prepare, isStale, apply };
}

/** 串行分片确认；在二次确认之前上锁，失败明细保留供原页重试。 */
export function createReviewSubmission(api, chunkSize = 5) {
  const busy = ref(false);
  const progress = ref({ visible: false, done: 0, total: 0 });
  async function submit(items, approve = async () => true) {
    if (busy.value || !items.length) return null;
    busy.value = true;
    const succeeded = [];
    const failed = [];
    const metadataWarnings = [];
    let uncertain = false;
    try {
      if (!await approve()) return null;
      progress.value = { visible: true, done: 0, total: items.length };
      for (let i = 0; i < items.length; i += chunkSize) {
        const slice = items.slice(i, i + chunkSize);
        try {
          const response = await api.batchConfirm({ items: slice });
          metadataWarnings.push(...(response?.metadataWarnings || []).filter((warning) => slice.some((item) => item.id === warning.id)));
          const ok = new Set(response?.succeeded || []);
          const errors = new Map((response?.failed || []).map((entry) => [entry.id, entry.message]));
          for (const item of slice) {
            if (ok.has(item.id) && !errors.has(item.id)) succeeded.push(item.id);
            else if (errors.has(item.id) && !ok.has(item.id)) failed.push({ id: item.id, message: errors.get(item.id) || '确认失败，请刷新核对记录状态' });
            else { uncertain = true; failed.push({ id: item.id, message: '确认响应缺项或互相矛盾，请刷新核对记录状态' }); }
          }
        } catch (cause) {
          uncertain = true;
          for (const item of items.slice(i)) failed.push({ id: item.id, message: `提交中断，请刷新核对后再试：${message(cause)}` });
          break;
        }
        progress.value.done = Math.min(i + slice.length, items.length);
        if (uncertain) {
          for (const item of items.slice(i + slice.length)) failed.push({ id: item.id, message: '前一批结果不确定，本项尚未提交，请刷新核对后再试' });
          break;
        }
      }
      return { succeeded, failed, uncertain, metadataWarnings };
    } finally {
      progress.value.visible = false;
      busy.value = false;
    }
  }
  return { busy, progress, submit };
}
