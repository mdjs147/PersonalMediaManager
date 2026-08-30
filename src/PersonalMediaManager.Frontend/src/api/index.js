// @ts-check
import { client, unwrap } from './http';

/**
 * 后端按 19 个 Controller 划分；前端按业务域聚合。
 *
 * 类型保护（P3-13 完整迁移）：
 * - 全部调用走 openapi-fetch 的 `client.GET/POST(path, { params, body, parseAs, ... })`
 * - path 字面量在 IDE 内由 schema.d.ts 实时校验：写错路径 / 漏 params.path / body 形状错配，IntelliSense 直接红线
 * - schema.d.ts 由 `npm run gen:api`（或 CI scripts/check-schema-drift.ps1）从后端 OpenAPI 自动重生
 * - unwrap() 统一拆 ApiResponse 信封：code=0 → 返 data；非 0 → ElMessage + reject
 * - 二进制流（nfo / system.export）走 `parseAs: 'blob'`，unwrap 见 data 非 envelope 直接返 Blob
 * - multipart（system.import / parseRules.import）用 `bodySerializer: (b) => b` 绕过 JSON 序列化
 *
 * 接口形状对齐说明（与 ASP.NET 后端约束）：
 * - 后端 Update / Delete 一律是 POST /update / POST /delete，Id 放 body；前端调用层把 id 合并进 payload，
 *   让页面 call site 仍以 `update(id, payload)` / `delete(id)` 形式书写
 * - 后端 URL 部分 slug 是 `watch/folders` / `watch/ignore-rules` / `ai-providers`，非短横线连接
 */

export const api = {
  setup: {
    status: () => unwrap(client.GET('/api/setup/status')),
    // 配置就绪度（向导断点续配 + Dashboard 完成度卡片）：{ isCompleted, hasAdmin, watchFolderCount, tmdbHasApiKey, categoryTotalCount, categoryUnsetCount, aiProviderCount }
    // 注：schema.d.ts 需主 agent 重生后此路径才有 TS 类型；重生前 IDE 对该 path 字面量报红属预期，运行不受影响。
    checklist: () => unwrap(client.GET('/api/setup/checklist')),
    createAdmin: (payload) => unwrap(client.POST('/api/setup/admin', { body: payload })),
    complete: () => unwrap(client.POST('/api/setup/complete')),
  },
  auth: {
    login: (payload) => unwrap(client.POST('/api/auth/login', { body: payload })),
    me: () => unwrap(client.GET('/api/auth/me')),
    logout: () => unwrap(client.POST('/api/auth/logout')),
  },
  account: {
    listUsers: () => unwrap(client.GET('/api/account/users')),
    createUser: (payload) => unwrap(client.POST('/api/account/users/create', { body: payload })),
    changePassword: (payload) => unwrap(client.POST('/api/account/password/change', { body: payload })),
    deleteUser: (id) => unwrap(client.POST('/api/account/users/{id}/delete', { params: { path: { id } } })),
  },
  dashboard: {
    stats: () => unwrap(client.GET('/api/dashboard/stats')),
    recent: (params) => unwrap(client.GET('/api/dashboard/recent', { params: { query: params } })),
    tasks: (params) => unwrap(client.GET('/api/dashboard/tasks', { params: { query: params } })),
    health: () => unwrap(client.GET('/api/dashboard/health')),
    heatmap: (params) => unwrap(client.GET('/api/dashboard/heatmap', { params: { query: params } })),
    watchFolderActivity: () => unwrap(client.GET('/api/dashboard/watch-folder-activity')),
  },
  // 统计分析（库构成 / 入库趋势 / 存储 Top 一次聚合，只读匿名）
  // 注：schema.d.ts 需主 agent 重生后此路径才有 TS 类型；重生前 IDE 对该 path 字面量报红属预期，运行不受影响。
  statistics: {
    // params = { range?: '30d'|'90d'|'1y'|'all' }（缺省 all，按归档时间窗过滤整页）
    overview: (params) => unwrap(client.GET('/api/statistics/overview', { params: { query: params } })),
  },
  review: {
    list: (params) => unwrap(client.GET('/api/review', { params: { query: params } })),
    confirm: (id, payload) => unwrap(client.POST('/api/review/{id}/confirm', {
      params: { path: { id } }, body: payload,
    })),
    ignore: (id, payload) => unwrap(client.POST('/api/review/{id}/ignore', {
      params: { path: { id } }, body: payload,
    })),
    batchConfirm: (payload) => unwrap(client.POST('/api/review/batch-confirm', { body: payload })),
    batchIgnore: (payload) => unwrap(client.POST('/api/review/batch-ignore', { body: payload })),
    tmdbSearch: (id, params) => unwrap(client.GET('/api/review/{id}/tmdb-search', {
      params: { path: { id }, query: params },
    })),
    bindTmdb: (id, payload) => unwrap(client.POST('/api/review/{id}/bind-tmdb', {
      params: { path: { id } }, body: payload,
    })),
    // 按 TMDB ID 取详情（人工填 ID 预览）：params = { tmdbId, mediaType }
    tmdbDetail: (id, params) => unwrap(client.GET('/api/review/{id}/tmdb-detail', {
      params: { path: { id }, query: params },
    })),
    // 去向预览（只读批量）：payload = { items:[{ key, tmdbId, mediaType, title, year, season, episode, episodeEnd, categoryId, fileName }] }
    previewPaths: (payload) => unwrap(client.POST('/api/review/preview-paths', { body: payload })),
    // 文件检查（批量）：payload = { items:[{ id, rowVersion }] }；返回 { removed:[id], kept, failed:[{ id, message }] }，不存在的源文件转 Ignored 移出队列
    checkFiles: (payload) => unwrap(client.POST('/api/review/check-files', { body: payload })),
  },
  history: {
    list: (params) => unwrap(client.GET('/api/history', { params: { query: params } })),
    // 处理队列：在途任务（排队 + 处理中），按 Id 正序（先进先出），出参结构同 list
    pending: (params) => unwrap(client.GET('/api/history/pending', { params: { query: params } })),
    detail: (id) => unwrap(client.GET('/api/history/{id}', { params: { path: { id } } })),
    cancel: (id) => unwrap(client.POST('/api/history/{id}/cancel', { params: { path: { id } } })),
    rescan: (id) => unwrap(client.POST('/api/history/{id}/rescan', { params: { path: { id } } })),
    // 批量重试：把所有 Failed 记录重投回队列（源文件不存在的跳过）
    rescanFailed: () => unwrap(client.POST('/api/history/rescan-failed')),
    // 重新处理：删旧记录后以全新流程重投源文件。单条 {ids:[id]} / 批量 {ids:[...]} / 整剧 {tmdbId, tmdbMediaType}
    reprocess: (payload) => unwrap(client.POST('/api/history/reprocess', { body: payload })),
    // 删除记录（仅终态可删，处理时间线级联删）：选择器同 reprocess（单条 / 批量 / 整剧）
    delete: (payload) => unwrap(client.POST('/api/history/delete', { body: payload })),
    /**
     * .nfo 二进制流（application/xml）：parseAs:'blob' 让 openapi-fetch 把响应解析为 Blob；
     * unwrap 见 data 非 ApiResponse 信封形（Blob 没有 .code 字段）直接返 Blob 给调用方。
     */
    nfo: (id) => unwrap(client.GET('/api/history/{id}/nfo', {
      params: { path: { id } }, parseAs: 'blob',
    })),
    // 测试路径：只读演算该记录归档去向并逐项体检（诊断「最后一部 / 越界集号复制提示路径错误」），不动文件
    previewArchive: (id) => unwrap(client.POST('/api/history/{id}/preview-archive', { params: { path: { id } } })),
    // 手动移动：用记录现有元数据直接重跑归档（mode='Move' 移动 / 'Copy' 复制保留源），不重新解析
    manualArchive: (id, mode = 'Move') => unwrap(client.POST('/api/history/{id}/manual-archive', {
      params: { path: { id } }, body: { mode },
    })),
    // 撤销归档（仅 Completed）：归档文件移回源位置 / 删归档副本，记录回退为 Skipped（未入库）
    undoArchive: (id) => unwrap(client.POST('/api/history/{id}/undo-archive', { params: { path: { id } } })),
    // 退回重改（仅 Completed/Skipped）：撤销归档并回退到 AwaitingReview 队列重新填资料
    reopen: (id) => unwrap(client.POST('/api/history/{id}/reopen', { params: { path: { id } } })),
    // 批量退回重改：选择器同 reprocess（批量 ids / 整剧 tmdbId），逐条移回源位 + 回 AwaitingReview；仅 Completed/Skipped 生效
    batchReopen: (payload) => unwrap(client.POST('/api/history/batch-reopen', { body: payload })),
    // 批量撤销归档：逐条移回源位 / 删归档副本 + 回 Skipped；仅 Completed 生效
    batchUndoArchive: (payload) => unwrap(client.POST('/api/history/batch-undo-archive', { body: payload })),
  },
  scan: {
    // force=true：遇已存在 MediaItem 自动重投 Failed（Failed→Queued + ClearError + 入队）；其余状态仍跳过
    trigger: ({ force = false } = {}) =>
      unwrap(client.POST('/api/scan/trigger', { params: { query: { force } } })),
    folder: (id) => unwrap(client.POST('/api/scan/folder/{id}', { params: { path: { id } } })),
    // 手动整理：任意文件 / 目录路径入队（不必是监控目录）
    path: (path) => unwrap(client.POST('/api/scan/path', { body: { path } })),
    // 整理演练：只读预览解析 / 匹配 / 命名，不调 AI、不动文件、不写库
    dryRun: (path) => unwrap(client.POST('/api/scan/dry-run', { body: { path } })),
  },
  library: {
    // 已归档作品海报墙（按 TMDB 聚合）；params 支持 type/categoryId/keyword/genreId/personId/companyId/networkId/keywordId/country/language/yearFrom/yearTo/sort/page/pageSize
    list: (params) => unwrap(client.GET('/api/library', { params: { query: params } })),
    // 库内可用筛选项（类型/演职员/公司/电视台/关键词/国家/语言）
    facets: () => unwrap(client.GET('/api/library/facets')),
    // 作品详情：富化元数据 + 关联 + 全部文件记录（params = { mediaType }）
    detail: (tmdbId, params) => unwrap(client.GET('/api/library/{tmdbId}', {
      params: { path: { tmdbId }, query: params },
    })),
    // 某季分集（每集简介/剧照 + 本地映射）；params = { mediaType }
    season: (tmdbId, seasonNumber, params) => unwrap(client.GET('/api/library/{tmdbId}/seasons/{seasonNumber}', {
      params: { path: { tmdbId, seasonNumber }, query: params },
    })),
    // 相关作品（按共享 类型/演职员/公司 计分）；params = { mediaType }
    related: (tmdbId, params) => unwrap(client.GET('/api/library/{tmdbId}/related', {
      params: { path: { tmdbId }, query: params },
    })),
    // 强制刷新单部富化元数据；params = { mediaType }
    refreshMetadata: (tmdbId, params) => unwrap(client.POST('/api/library/{tmdbId}/refresh-metadata', {
      params: { path: { tmdbId }, query: params },
    })),
    // 整库富化刷新（供库内搜索覆盖历史作品）
    refreshMetadataAll: () => unwrap(client.POST('/api/library/refresh-metadata-all')),
    // 整库文件存在性扫描（落 FileMissing 供打缺失标记）
    scanExistence: () => unwrap(client.POST('/api/library/scan-existence')),
    // 存量音频重扫（对未探测过的已完成记录批量 ffprobe 探测不兼容音轨并打标）
    rescanAudio: () => unwrap(client.POST('/api/library/rescan-audio')),
    // 孤儿文件扫描：遍历各分类归档根，列出磁盘有、DB 无记录的媒体文件（删了记录但文件留在库里）
    scanOrphans: () => unwrap(client.POST('/api/library/orphans/scan')),
    // 孤儿认领：把选中孤儿文件重新投入管线重新登记入库；payload = { paths:[...] }
    claimOrphans: (payload) => unwrap(client.POST('/api/library/orphans/claim', { body: payload })),
    // 海报图 URL（给 <img src> 用，相对路径同源；无缓存时端点 404，前端降级占位）
    posterUrl: (tmdbId) => `/api/library/poster/${tmdbId}`,
    // TMDB 图片代理 URL（背景图/人物照/剧照/Logo，按 size + 相对路径取）
    imageUrl: (size, path) => (path ? `/api/library/tmdb-image/${size}/${String(path).replace(/^\/+/, '')}` : ''),
  },
  logs: {
    list: (params) => unwrap(client.GET('/api/logs', { params: { query: params } })),
  },
  // 统一全局搜索（顶栏）：一次返回历史 / 媒体库 / 待确认三域各 topN 命中 + 各域真实总数
  // params = { q, limitPerGroup?, scopes? }；q 空 / 纯空白 → 后端返回空结果（前端也已对空串短路）
  // 注：schema.d.ts 未含 /api/search（需主 agent 重生 OpenAPI 类型）；IDE 内此路径暂红，运行无影响
  search: {
    query: (params) => unwrap(client.GET('/api/search', { params: { query: params } })),
  },
  watchFolders: {
    list: () => unwrap(client.GET('/api/settings/watch/folders')),
    create: (payload) => unwrap(client.POST('/api/settings/watch/folders', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/watch/folders/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/watch/folders/delete', { body: { id } })),
    test: (id) => unwrap(client.GET('/api/settings/watch/folders/{id}/test', { params: { path: { id } } })),
  },
  mediaExtensions: {
    list: () => unwrap(client.GET('/api/settings/media-extensions')),
    create: (payload) => unwrap(client.POST('/api/settings/media-extensions', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/media-extensions/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/media-extensions/delete', { body: { id } })),
  },
  watchIgnoreRules: {
    list: () => unwrap(client.GET('/api/settings/watch/ignore-rules')),
    create: (payload) => unwrap(client.POST('/api/settings/watch/ignore-rules', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/watch/ignore-rules/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/watch/ignore-rules/delete', { body: { id } })),
  },
  categories: {
    list: () => unwrap(client.GET('/api/settings/categories')),
    create: (payload) => unwrap(client.POST('/api/settings/categories', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/categories/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/categories/delete', { body: { id } })),
  },
  categoryMatchRules: {
    list: (params) => unwrap(client.GET('/api/settings/category-match-rules', { params: { query: params } })),
    create: (payload) => unwrap(client.POST('/api/settings/category-match-rules', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/category-match-rules/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/category-match-rules/delete', { body: { id } })),
  },
  parseRules: {
    list: () => unwrap(client.GET('/api/settings/parse-rules')),
    listBuiltin: () => unwrap(client.GET('/api/settings/parse-rules/builtin')),
    create: (payload) => unwrap(client.POST('/api/settings/parse-rules', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/parse-rules/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/parse-rules/delete', { body: { id } })),
    test: (payload) => unwrap(client.POST('/api/settings/parse-rules/test', { body: payload })),
    /**
     * 批量导入：multipart/form-data，文件为 JSON 数组；mode = Merge | Replace
     * bodySerializer: (b) => b 绕过 JSON 序列化，让 fetch 直接吃 FormData 设 boundary
     * @param {File} file
     * @param {'Merge'|'Replace'} mode
     */
    import: (file, mode = 'Merge') => {
      const fd = new FormData();
      fd.append('file', file);
      return unwrap(client.POST('/api/settings/parse-rules/import', {
        params: { query: { mode } },
        body: /** @type {any} */ (fd),
        bodySerializer: (b) => b,
      }));
    },
  },
  parseTestCases: {
    list: (params) => unwrap(client.GET('/api/settings/parse-testcases', { params: { query: params } })),
    get: (id) => unwrap(client.GET('/api/settings/parse-testcases/{id}', { params: { path: { id } } })),
    create: (payload) => unwrap(client.POST('/api/settings/parse-testcases', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/parse-testcases/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/parse-testcases/delete', { body: { id } })),
    importFromFailed: (payload) => unwrap(client.POST('/api/settings/parse-testcases/import-from-failed', { body: payload })),
    promote: (id, rowVersion) => unwrap(client.POST('/api/settings/parse-testcases/promote', { body: { id, rowVersion } })),
    disable: (id, rowVersion) => unwrap(client.POST('/api/settings/parse-testcases/disable', { body: { id, rowVersion } })),
    reset: (id, rowVersion) => unwrap(client.POST('/api/settings/parse-testcases/reset', { body: { id, rowVersion } })),
    run: (id) => unwrap(client.POST('/api/settings/parse-testcases/run', { body: { id } })),
    runBatch: (payload) => unwrap(client.POST('/api/settings/parse-testcases/run-all', { body: payload })),
    approve: (id, rowVersion) => unwrap(client.POST('/api/settings/parse-testcases/approve', { body: { id, rowVersion } })),
    triage: (id) => unwrap(client.POST('/api/settings/parse-testcases/triage', { body: { id } })),
    suggestRule: (id) => unwrap(client.POST('/api/settings/parse-testcases/suggest-rule', { body: { id } })),
  },
  parseAiProviders: {
    list: () => unwrap(client.GET('/api/settings/ai-providers')),
    create: (payload) => unwrap(client.POST('/api/settings/ai-providers', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/ai-providers/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/ai-providers/delete', { body: { id } })),
    test: (id) => unwrap(client.POST('/api/settings/ai-providers/test', { body: { id } })),
    enable: (id) => unwrap(client.POST('/api/settings/ai-providers/enable', { body: { id } })),
    // 重置套餐用量：清零已用调用次数 / token 并解除超限自动禁用（新套餐周期开始时用）
    // 注：schema.d.ts 需主 agent 重生后此路径才有 TS 类型；重生前 IDE 对该 path 字面量报红属预期，运行不受影响。
    resetQuota: (id) => unwrap(client.POST('/api/settings/ai-providers/reset-quota', { body: { id } })),
    stats: (id, windowHours = 24) => unwrap(client.GET('/api/settings/ai-providers/{id}/stats', {
      params: { path: { id }, query: { windowHours } },
    })),
    // 分页调用日志（含原文）：query 支持 page/pageSize/success/errorType/chainId/from/to
    logs: (id, query = {}) => unwrap(client.GET('/api/settings/ai-providers/{id}/logs', {
      params: { path: { id }, query },
    })),
  },
  tmdbSetting: {
    get: () => unwrap(client.GET('/api/settings/tmdb')),
    update: (payload) => unwrap(client.POST('/api/settings/tmdb/update', { body: payload })),
    test: () => unwrap(client.POST('/api/settings/tmdb/test')),
    clearCache: () => unwrap(client.POST('/api/settings/tmdb/cache/clear')),
  },
  // 字幕下载（Assrt 字幕源）：源设置 + 按媒体记录的候选搜索 / 文件列表 / 下载落地 / 已下载记录
  subtitles: {
    // 字幕源设置：返回 { hasToken, baseUrl, timeoutSeconds, useProxy }
    setting: () => unwrap(client.GET('/api/settings/subtitle')),
    // 更新设置；token 三态：不传 / null = 不变、空串 = 清空、非空 = 设置
    updateSetting: (payload) => unwrap(client.POST('/api/settings/subtitle/update', { body: payload })),
    // 测试连接：后端统一返 200 + data.{ success, quota, elapsedMilliseconds, errorMessage }，失败不抛异常，调用方必须看 success
    testSetting: () => unwrap(client.POST('/api/settings/subtitle/test')),
    // 候选字幕搜索：keyword 可空（后端缺省用该记录原始文件名）
    search: (id, params) => unwrap(client.GET('/api/media/{id}/subtitles/search', {
      params: { path: { id }, query: params },
    })),
    // 某候选的文件列表：params = { subtitleId }；返回 items[].index = -1 表示单文件
    files: (id, params) => unwrap(client.GET('/api/media/{id}/subtitles/files', {
      params: { path: { id }, query: params },
    })),
    // 下载落地到归档目录：payload = { subtitleId, fileIndex, subtitleName } → { savedFileName, savedPath, language }
    download: (id, payload) => unwrap(client.POST('/api/media/{id}/subtitles/download', {
      params: { path: { id } }, body: payload,
    })),
    // 该记录已下载字幕列表
    downloads: (id) => unwrap(client.GET('/api/media/{id}/subtitles/downloads', { params: { path: { id } } })),
  },
  generalSettings: {
    get: () => unwrap(client.GET('/api/settings/general')),
    update: (payload) => unwrap(client.POST('/api/settings/general/update', { body: payload })),
    // ffmpeg 路径自检：pathOverride 传当前输入框值（未保存也能测）；后端统一返 200 + data.success，失败不抛，调用方必须看 success
    // 注：schema.d.ts 需主 agent 重生后此路径才有 TS 类型；重生前 IDE 对该 path 字面量报红属预期，运行不受影响。
    testFfmpeg: (pathOverride) => unwrap(client.POST('/api/settings/general/test-ffmpeg', { body: { pathOverride } })),
  },
  // 归档命名模板（自定义 Plex 命名）：读 / 写 4 个 Archive_ 模板 key + 草稿预览（固定示例渲染，含校验错误清单）
  // 注：schema.d.ts 需主 agent 重生后这些路径才有 TS 类型（新增 POST/GET 端点）；重生前 IDE 对下列 path 字面量报红属预期，运行不受影响。
  archiveNaming: {
    get: () => unwrap(client.GET('/api/settings/archive-naming')),
    update: (payload) => unwrap(client.POST('/api/settings/archive-naming/update', { body: payload })),
    preview: (payload) => unwrap(client.POST('/api/settings/archive-naming/preview', { body: payload })),
  },
  webhooks: {
    getEnabled: () => unwrap(client.GET('/api/settings/webhooks/enabled')),
    setEnabled: (enabled) => unwrap(client.POST('/api/settings/webhooks/enabled', { body: { enabled } })),
    list: () => unwrap(client.GET('/api/settings/webhooks')),
    create: (payload) => unwrap(client.POST('/api/settings/webhooks', { body: payload })),
    update: (id, payload) => unwrap(client.POST('/api/settings/webhooks/update', {
      body: { ...payload, id },
    })),
    delete: (id) => unwrap(client.POST('/api/settings/webhooks/delete', { body: { id } })),
    test: (id) => unwrap(client.POST('/api/settings/webhooks/{id}/test', { params: { path: { id } } })),
    listDeliveries: (subscriptionId, { skip = 0, take = 50, status = undefined } = {}) =>
      unwrap(client.GET('/api/settings/webhooks/deliveries', {
        params: { query: { subscriptionId, skip, take, status } },
      })),
  },
  system: {
    /** 匿名版本号端点：登录前 / 关于对话框使用 */
    version: () => unwrap(client.GET('/api/system/version')),
    info: () => unwrap(client.GET('/api/system/info')),
    /**
     * 导出 zip 二进制流：parseAs:'blob' 让 openapi-fetch 直接 .blob() 解析响应；
     * unwrap 见 Blob 非 envelope 直接返 Blob 给调用方。
     * @returns {Promise<Blob>}
     */
    export: () => unwrap(client.POST('/api/system/export', { parseAs: 'blob' })),
    /**
     * 上传 zip 导入：multipart/form-data；bodySerializer 绕过 JSON 序列化
     * @param {File} file
     */
    import: (file) => {
      const fd = new FormData();
      fd.append('file', file);
      return unwrap(client.POST('/api/system/import', {
        body: /** @type {any} */ (fd),
        bodySerializer: (b) => b,
      }));
    },
    clearHistory: () => unwrap(client.POST('/api/system/clear-history')),
    resetConfig: () => unwrap(client.POST('/api/system/reset-config')),
    /** 立即备份：VACUUM INTO 在线快照 + 密钥环打包到 backups 目录，按保留份数清理旧备份 */
    backup: () => unwrap(client.POST('/api/system/backup')),
    /** 列出 backups/ 内备份 zip（时间倒序），供「从备份恢复」选择 */
    backups: () => unwrap(client.GET('/api/system/backups')),
    /** 从指定备份恢复（暂存待重启换库）：fileName 为 backups/ 内 pmm-backup-*.zip 文件名 */
    restore: (fileName) => unwrap(client.POST('/api/system/restore', { body: { fileName } })),
  },
  systemUpdateCheck: {
    /** GET /api/system/update-check 取设置 + 上次结果 */
    get: () => unwrap(client.GET('/api/system/update-check')),
    /** PUT /api/system/update-check/settings 更新 owner/repo/pat（pat: null=不变 / ""=清空 / 非空=替换） */
    updateSettings: (payload) => unwrap(client.PUT('/api/system/update-check/settings', { body: payload })),
    /** POST /api/system/update-check/run 立即检查 + 写库 */
    run: () => unwrap(client.POST('/api/system/update-check/run')),
    /** POST /api/system/update-check/test 临时密钥试一次 */
    test: (payload) => unwrap(client.POST('/api/system/update-check/test', { body: payload })),
    /** POST /api/system/update-check/skip 跳过此版本 */
    skip: (version) => unwrap(client.POST('/api/system/update-check/skip', { body: { version } })),
  },
};

export default api;
