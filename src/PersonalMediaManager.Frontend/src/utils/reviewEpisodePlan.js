/** 审核页批填计划：所有操作先预览，不推断作品身份，不直接归档。 */
export function readEpisodeInfo(raw) {
  try {
    const value = typeof raw === 'string' ? JSON.parse(raw) : raw;
    return Object.fromEntries(['title', 'season', 'episode', 'episodeEnd'].map((key) => [key,
      value?.[key] ?? value?.[key[0].toUpperCase() + key.slice(1)] ?? null,
    ]));
  } catch { return { title: null, season: null, episode: null, episodeEnd: null }; }
}

export function defaultEpisodeOptions() {
  return { mode: 'rules', season: null, start: 1, step: 1, keepExisting: true };
}

export function naturalFileOrder(rows) {
  return [...rows].sort((a, b) => (a.fileName || '').localeCompare(b.fileName || '', 'zh', { numeric: true, sensitivity: 'base' })
    || (a.sourcePath || '').localeCompare(b.sourcePath || '', 'zh', { numeric: true }) || a.id - b.id);
}

export function episodeLabel(value) {
  const show = (n) => n == null ? '—' : String(n).padStart(2, '0');
  return `S${show(value?.season)} E${show(value?.episode)}${value?.episodeEnd != null ? `–E${show(value.episodeEnd)}` : ''}`;
}

const fields = ['season', 'episode', 'episodeEnd'];
const numbers = (value) => Object.fromEntries(fields.map((key) => [key, value?.[key] ?? null]));
const changed = (before, after) => fields.some((key) => before[key] !== after[key]);

/** 指纹同时覆盖选中范围、作品、原字段和选项，阻止异步旧响应覆盖新编辑。 */
export function episodePlanFingerprint(rows, edits, options, identity = '') {
  return JSON.stringify({ identity, options, rows: naturalFileOrder(rows).map((row) => ({
    id: row.id, rowVersion: row.rowVersion, fileName: row.fileName, sourcePath: row.sourcePath,
    checked: !!edits[row.id]?.checked, ...numbers(edits[row.id]),
  })) });
}

export function episodeProblems(rows, edits) {
  const errors = {};
  const selected = rows.filter((row) => edits[row.id]?.checked);
  for (const row of selected) {
    const e = edits[row.id];
    if (!Number.isInteger(e.season) || e.season < 0 || e.season > 999) errors[row.id] = '季号须为 0～999 的整数';
    else if (!Number.isInteger(e.episode) || e.episode < 0 || e.episode > 9999) errors[row.id] = '集号须为 0～9999 的整数';
    else if (e.episodeEnd != null && (!Number.isInteger(e.episodeEnd) || e.episodeEnd < e.episode || e.episodeEnd > 9999)) errors[row.id] = '末集须不小于集号，且不超过 9999';
  }
  for (let i = 0; i < selected.length; i += 1) {
    const a = selected[i];
    const ae = edits[a.id];
    if (ae.season == null || ae.episode == null) continue;
    for (const b of selected.slice(i + 1)) {
      const be = edits[b.id];
      if (ae.season === be.season && be.episode != null && ae.episode <= (be.episodeEnd ?? be.episode) && be.episode <= (ae.episodeEnd ?? ae.episode)) {
        errors[a.id] = `季集重复或区间重叠：${b.fileName}`;
        errors[b.id] = `季集重复或区间重叠：${a.fileName}`;
      }
    }
  }
  return errors;
}

/** 只对勾选行产生计划；自然排序可预见，特别篇、跨季和区间不做静默重编号。 */
export function buildEpisodePlan(rows, edits, options, hints = []) {
  const selected = naturalFileOrder(rows.filter((row) => edits[row.id]?.checked));
  const mode = options.mode;
  const titles = new Set(selected.map((row) => readEpisodeInfo(row.parsedInfo).title?.normalize('NFC').trim().toLocaleLowerCase()).filter(Boolean));
  const seasons = new Set(selected.map((row) => edits[row.id]?.season).filter((value) => value != null));
  let globalError = '';
  if (!['rules', 'season', 'sequence'].includes(mode)) globalError = '请选择有效的批填方式';
  if (mode !== 'rules' && titles.size > 1 && !options.sameWorkConfirmed) globalError = '选中项含多个解析标题，请先核对同一作品并勾选确认，或缩小范围';
  if (mode === 'sequence' && seasons.size > 1) globalError = '选中项跨季，请按季分别勾选后编号';
  if (mode !== 'rules' && options.season != null && (!Number.isInteger(options.season) || options.season < 0 || options.season > 999)) globalError = '统一季号须为 0～999 的整数';
  if (mode === 'season' && options.season == null) globalError = '请指定统一季号';
  if (mode === 'sequence' && (!Number.isInteger(options.start) || options.start < 0 || options.start > 9999 || !Number.isInteger(options.step) || options.step < 1 || options.step > 9999)) globalError = '起始集号须为 0～9999，步长须为 1～9999 的整数';
  const hintMap = new Map(hints.map((hint) => [hint.id, hint]));
  const entries = selected.map((row, index) => {
    const before = numbers(edits[row.id]);
    const after = { ...before };
    const hint = hintMap.get(row.id);
    let error = globalError;
    const warnings = [];
    let source = mode === 'rules' ? 'LocalRules' : mode === 'season' ? 'ManualBulkSeason' : 'ManualBulkSequence';
    if (mode === 'rules') {
      if (!hint) error ||= '未返回该文件的规则证据，请重试';
      else if (hint.error) error ||= hint.error;
      else {
        for (const key of fields) {
          if (hint[key] != null && (!options.keepExisting || before[key] == null)) after[key] = hint[key];
          if (hint[key] != null && options.keepExisting && before[key] != null && before[key] !== hint[key]) warnings.push(`保留已有${key === 'season' ? '季号' : key === 'episode' ? '集号' : '末集'}（规则建议 ${hint[key]}）`);
        }
        if (fields.every((key) => hint[key] == null)) error ||= '没有可用的季集证据，未猜测填值';
      }
    } else {
      if (options.season != null && (!options.keepExisting || before.season == null)) after.season = options.season;
      if (mode === 'sequence' && (!options.keepExisting || before.episode == null)) {
        if (before.episodeEnd != null) error ||= '合并多集文件需逐项核对区间，未自动重编号';
        else after.episode = options.start + index * options.step;
      }
      if (before.season === 0 && after.season !== 0) error ||= '特别篇不能批量改为正片季，请单独核对';
      if (before.season != null && after.season !== before.season) warnings.push('季号将覆盖已有值，请确认不是跨季或特别篇');
    }
    return { id: row.id, fileName: row.fileName, before, after, source, evidence: hint?.evidence ?? null, error, warnings, changed: changed(before, after) };
  });
  const planned = { ...edits };
  for (const entry of entries) planned[entry.id] = { ...edits[entry.id], ...(entry.error ? entry.before : entry.after) };
  const problems = episodeProblems(rows, planned);
  // 缺字段允许分步补全，但无效数字与重复落点必须当场阻止。
  for (const entry of entries) {
    const after = entry.after;
    const invalid = fields.some((key) => after[key] != null && (!Number.isInteger(after[key]) || after[key] < 0 || after[key] > (key === 'season' ? 999 : 9999)))
      || (after.episodeEnd != null && after.episode != null && after.episodeEnd < after.episode);
    if (invalid || problems[entry.id]?.startsWith('季集重复')) entry.error ||= problems[entry.id] || '季集范围无效';
    entry.changed = !entry.error && changed(entry.before, entry.after);
  }
  return entries;
}
