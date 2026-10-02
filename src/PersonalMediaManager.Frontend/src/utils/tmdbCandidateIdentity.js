/** 类型是 TMDB 身份的一部分；缺失或未知类型不得猜成电影。 */
export function normalizeTmdbMediaType(value) {
  const type = typeof value === 'string' ? value.trim().toLowerCase() : '';
  return type === 'tv' ? 'Tv' : type === 'movie' ? 'Movie' : null;
}

export function tmdbCandidateIdentity(candidate) {
  const mediaType = normalizeTmdbMediaType(candidate?.mediaType);
  const rawId = candidate?.tmdbId;
  const tmdbId = typeof rawId === 'number' || (typeof rawId === 'string' && /^\d+$/.test(rawId)) ? Number(rawId) : NaN;
  return mediaType && Number.isSafeInteger(tmdbId) && tmdbId > 0 ? { mediaType, tmdbId } : null;
}

export function tmdbCandidateKey(candidate) {
  const identity = tmdbCandidateIdentity(candidate);
  return identity ? `${identity.mediaType}:${identity.tmdbId}` : null;
}

export function sameTmdbCandidate(left, right) {
  const key = tmdbCandidateKey(left);
  return key !== null && key === tmdbCandidateKey(right);
}

export function findTmdbCandidate(candidates, selection) {
  return candidates.find((candidate) => sameTmdbCandidate(candidate, selection)) ?? null;
}

/** 保留原排序，按联合身份取并集；不让不可验证类型进入可选列表。 */
export function mergeTmdbCandidates(candidates) {
  const merged = new Map();
  for (const candidate of candidates) {
    const identity = tmdbCandidateIdentity(candidate);
    if (!identity) continue;
    const key = tmdbCandidateKey(identity);
    if (!merged.has(key)) merged.set(key, { ...candidate, ...identity });
  }
  return [...merged.values()];
}

/** 手动搜索只补充同一类型与 ID 的作品，不覆盖另一类型的同号条目。 */
export function upsertTmdbCandidate(candidates, result) {
  const identity = tmdbCandidateIdentity(result);
  if (!identity) return null;
  const existing = findTmdbCandidate(candidates, identity);
  const supplied = Object.fromEntries(Object.entries(result).filter(([, value]) => value != null));
  const card = { ...existing, ...supplied, ...identity };
  if (existing) {
    Object.assign(existing, card);
    return existing;
  }
  candidates.unshift(card);
  return card;
}
