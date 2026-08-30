<script setup>
import { ElMessage } from 'element-plus';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

/**
 * 强制匹配标识（pmm.txt / TMDB URL）说明页（识别归类）
 *
 * 纯静态文档 + 样例下载，无后端调用：
 *   - 展示 pmm.txt 支持的网址形态与高级 key=value 覆盖；
 *   - 一键下载可直接编辑的样例 pmm.txt（含注释，换掉网址即用）；
 *   - 展示归档落点样例与 Plex 兼容说明。
 * 对应后端：ForcedMatchMarkerParser / IForcedMatchMarkerStore / ProcessFileService 强制匹配短路。
 */

// 可下载 / 可复制的样例（含 BOM，兼容 Windows 记事本打开 CJK 注释）
const SAMPLE_PMM_TXT = `﻿# PMM 强制匹配标识
# 用法：把本文件放在剧集所在文件夹，下面这行换成该剧在 TMDB 的页面网址即可。
# 该文件夹内所有剧集会强制锚定到此 TMDB 条目，跳过 AI 与规则的自动判断。

https://www.themoviedb.org/tv/20111-seed/episode_group/5deddbdcdc86470011c4bb7d/group/5deddbfedaf57c0015ed4e0a

# ── 支持的网址形态（任选其一，从浏览器地址栏直接复制）──
#   剧集组(重制版/特殊编组)  /tv/{id}/episode_group/{eg}/group/{g}
#   指定季                   /tv/{id}/season/{n}
#   仅锚剧集                 /tv/{id}
#   电影                     /movie/{id}
#
# ── 高级覆盖（少用，可与上面的网址混写，显式项优先）──
#   tmdb   = 20111         # TMDB 数字 id
#   type   = tv            # tv / movie，默认 tv
#   season = 1             # 强制归到此季
#   title  = 自定义剧名     # 覆盖归档文件夹标题
`;

const URL_FORMS = [
  { url: '/tv/{id}/episode_group/{eg}/group/{g}', result: '剧集 + 剧集组 + 分组（重制版 / 特殊编组，按编组把集号翻译成正典季集）' },
  { url: '/tv/{id}/season/{n}', result: '剧集 + 强制归到第 n 季' },
  { url: '/tv/{id}', result: '剧集（仅锚定 series，季 / 集仍按文件名解析）' },
  { url: '/movie/{id}', result: '电影' },
];

// 载体二：文件夹名 / 文件名 {tmdb-NNN} 标记（仅锚 id，类型/季/集仍由系统识别）
const MARKER_NAME_FORMS = [
  { name: '刀剑神域 (2012) {tmdb-45782}/', effect: '文件夹名锚定到 TMDB 45782；剧 / 影、季、集仍由系统识别' },
  { name: '流浪地球 (2019) {tmdb-535292}.mkv', effect: '文件名也认（扩展名不影响）；电影单文件尤其方便' },
  { name: '盗梦空间 (2010) {tmdb-27205}', effect: '目录名 / 文件名带标记即锚定' },
  { name: '某剧 [tmdb-1399]', effect: '方括号 / [tmdbid-] / 大写写法同样识别' },
];

const KV_FIELDS = [
  { key: 'tmdb', desc: 'TMDB 数字 id（必填，或由网址提供）', sample: '20111' },
  { key: 'type', desc: 'tv / movie，默认 tv', sample: 'tv' },
  { key: 'season', desc: '强制归到此季', sample: '1' },
  { key: 'episode_group', desc: '剧集组 id', sample: '5deddbdc…' },
  { key: 'group', desc: '剧集组下的分组 id（多分组时指定）', sample: '5deddbfe…' },
  { key: 'title', desc: '覆盖归档文件夹标题', sample: '机动战士高达SEED' },
];

const SAMPLE_TREE = `<分类根>/
└─ 机动战士高达SEED (2002) {tmdb-20111}/      ← 剧集根（{tmdb-NNN} = Plex 官方匹配标记）
   ├─ tvshow.nfo / poster.jpg / fanart.jpg     ← 元数据 + 海报随之生成
   └─ Season 01/
      ├─ 机动战士高达SEED (2002) - S01E01.mkv   ← 集号已按剧集组翻译成 TMDB 正典
      ├─ 机动战士高达SEED (2002) - S01E01.nfo
      └─ …`;

function downloadSample() {
  const blob = new Blob([SAMPLE_PMM_TXT], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'pmm.txt';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
  ElMessage.success('已下载样例 pmm.txt');
}

function copyText(text) {
  // 复制去掉 BOM 的纯文本
  const clean = String(text).replace(/^﻿/, '');
  if (navigator.clipboard?.writeText) {
    navigator.clipboard.writeText(clean).then(
      () => ElMessage.success('已复制'),
      () => ElMessage.error('复制失败，请手动选择'),
    );
  } else {
    ElMessage.error('当前环境不支持自动复制，请手动选择');
  }
}
</script>

<template>
  <div class="page">
    <PmmPageHeader
      eyebrow="识别归类"
      title="强制匹配标识（pmm.txt / 文件夹名）"
      subtitle="规则与 AI 都易判错的特殊片（重制版 / 剧集组 / 冷门片）：放一个 pmm.txt 贴 TMDB 网址锁定全部信息，或直接把 {tmdb-NNN} 写进文件夹名只锚 id、其余照常识别。"
    >
      <template #actions>
        <button class="btn btn-primary btn-sm" @click="downloadSample">
          <PmmIcon name="download" :size="14" /> 下载样例 pmm.txt
        </button>
      </template>
    </PmmPageHeader>

    <!-- 怎么用 -->
    <div class="card">
      <div class="card-title"><PmmIcon name="shield" :size="16" /> 怎么用</div>
      <ol class="steps">
        <li>在媒体所在文件夹（自文件目录可逐层上溯到监控根）建一个 <code>pmm.txt</code>。</li>
        <li>把对应 TMDB 页面的网址整条贴进去（浏览器地址栏直接复制），存盘。</li>
        <li>该文件夹内所有剧集即按此条目强制锚定，<b>跳过规则与 AI 的自动判断</b>。</li>
      </ol>
      <div class="hint">
        <PmmIcon name="info" :size="14" />
        要记的只有两件事：<b>文件叫 <code>pmm.txt</code></b>、<b>里面放 TMDB 网址</b>。也认含 themoviedb.org 网址的 <code>.url</code> 快捷方式。
      </div>
    </div>

    <!-- 更省事：文件名 / 文件夹名标记 -->
    <div class="card">
      <div class="card-title"><PmmIcon name="folder" :size="16" /> 更省事：写进文件夹名或文件名（仅锚 id）</div>
      <p class="lead">
        不想建标识文件？把 TMDB 数字 id 以 <code>{tmdb-NNN}</code> 写进<b>文件夹名或文件名</b>即可——<b>只锚定到这个 TMDB 条目</b>，
        电视剧 / 电影类型、季号、集号<b>照常由系统识别</b>。电影常是单文件直接命名，文件名标记尤其方便。
      </p>
      <table class="table">
        <thead>
          <tr><th style="width: 50%">文件夹名 / 文件名示例</th><th>效果</th></tr>
        </thead>
        <tbody>
          <tr v-for="f in MARKER_NAME_FORMS" :key="f.name">
            <td><span class="font-mono tag tag-info">{{ f.name }}</span></td>
            <td class="muted-text">{{ f.effect }}</td>
          </tr>
        </tbody>
      </table>
      <div class="hint">
        <PmmIcon name="info" :size="14" />
        <code>{tmdb-NNN}</code> 正是本工具归档后产出的标准标记，所以<b>把已归档的整个剧集 / 电影文件夹、或单个电影文件再丢回监控目录，会被自动认出、零搜索零 AI</b>。
        类型若一时识别反（剧 ↔ 影），系统会自动换另一类型再试。
      </div>
      <div class="hint">
        <PmmIcon name="info" :size="14" />
        优先级 <code>pmm.txt</code> &gt; <code>.url</code> &gt; 文件名标记 &gt; 文件夹名标记 —— 同时存在时前者优先（<code>pmm.txt</code> / 网址能额外锁定类型 / 季 / 剧集组）。
      </div>
    </div>

    <!-- 网址形态 -->
    <div class="card">
      <div class="card-title"><PmmIcon name="link" :size="16" /> 支持的网址形态</div>
      <table class="table">
        <thead>
          <tr><th style="width: 44%">网址路径（贴整条）</th><th>解析结果</th></tr>
        </thead>
        <tbody>
          <tr v-for="f in URL_FORMS" :key="f.url">
            <td><span class="font-mono tag tag-info">{{ f.url }}</span></td>
            <td class="muted-text">{{ f.result }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- 样例文件 -->
    <div class="card">
      <div class="card-title">
        <PmmIcon name="download" :size="16" /> 样例 pmm.txt
        <div class="spacer" />
        <button class="btn btn-ghost btn-sm" @click="copyText(SAMPLE_PMM_TXT)">复制</button>
        <button class="btn btn-primary btn-sm" @click="downloadSample"><PmmIcon name="download" :size="13" /> 下载</button>
      </div>
      <pre class="code-block">{{ SAMPLE_PMM_TXT.replace(/^﻿/, '') }}</pre>
    </div>

    <!-- 高级覆盖 -->
    <div class="card">
      <div class="card-title"><PmmIcon name="edit" :size="16" /> 高级覆盖（少用，可与网址混写，显式项优先）</div>
      <table class="table">
        <thead>
          <tr><th style="width: 150px">键</th><th>说明</th><th style="width: 170px">示例</th></tr>
        </thead>
        <tbody>
          <tr v-for="k in KV_FIELDS" :key="k.key">
            <td><span class="font-mono tag tag-neutral">{{ k.key }}</span></td>
            <td class="muted-text">{{ k.desc }}</td>
            <td><span class="font-mono small">{{ k.sample }}</span></td>
          </tr>
        </tbody>
      </table>
      <div class="hint">
        注释行 <code>#</code> <code>//</code> <code>;</code> 与空行忽略；裸数字一行当作 tmdb id；类型缺省 <code>tv</code>。
      </div>
    </div>

    <!-- 归档落点样例 + Plex -->
    <div class="card">
      <div class="card-title"><PmmIcon name="folder" :size="16" /> 归档落点样例（以高达 SEED HD Remaster 剧集组为例）</div>
      <pre class="code-block tree">{{ SAMPLE_TREE }}</pre>
      <div class="plex-note">
        <div class="plex-note-title"><PmmIcon name="check" :size="14" /> Plex 能正确识别</div>
        <ul>
          <li><b><code>{tmdb-20111}</code> 是 Plex 官方匹配标记</b>：库的刮削器选 <b>「Plex TV Series」/「The Movie Database」(TMDB 系)</b> 即直接锚到该剧，无需额外插件。</li>
          <li><code>Season&nbsp;NN</code> + <code>SxxEyy</code> 是 Plex 标准分集命名；<b>集号已按剧集组翻译成 TMDB 正典</b>，在线刮削命中的就是对的那一集。</li>
          <li>同时生成 <code>tvshow.nfo</code> / 海报 / 单集 nfo，Plex 的「Local Media Assets」会一并采用（不依赖 .nfo 也能正确识别）。</li>
          <li class="warn"><PmmIcon name="info" :size="13" /> 注意：库的 agent 别选老的 <b>TheTVDB</b>（它认 <code>{tvdb-}</code> 而非 <code>{tmdb-}</code>）。</li>
        </ul>
      </div>
      <div class="hint">
        <PmmIcon name="info" :size="14" />
        文件名里的 <code>E01</code> 取决于剧集组把该集映射到正典第几集（编组常会重排，未必等于文件原集号）；实际映射以 TMDB 剧集组为准，可在「处理历史 / 详情时间线」查看。
      </div>
    </div>
  </div>
</template>

<style scoped lang="scss">
.card { margin-bottom: 16px; }
.card-title {
  display: flex; align-items: center; gap: 8px;
  font-weight: 600; font-size: 14px; color: var(--text); margin-bottom: 12px;
}
.spacer { flex: 1; }
.lead { margin: 0 0 12px; color: var(--text); font-size: 13.5px; line-height: 1.7; }
.lead code { font-family: var(--font-mono, monospace); background: var(--bg-subtle, rgba(127,127,127,.12)); padding: 1px 5px; border-radius: 4px; font-size: 12.5px; }
.steps { margin: 0; padding-left: 20px; display: flex; flex-direction: column; gap: 8px; color: var(--text); font-size: 13.5px; }
.steps code, .hint code, .plex-note code { font-family: var(--font-mono, monospace); background: var(--bg-subtle, rgba(127,127,127,.12)); padding: 1px 5px; border-radius: 4px; font-size: 12.5px; }
.hint {
  display: flex; align-items: center; gap: 6px; flex-wrap: wrap;
  margin-top: 12px; padding: 9px 12px; border-radius: 8px;
  background: var(--bg-subtle, rgba(127,127,127,.08)); color: var(--text-muted); font-size: 12.5px;
}
.muted-text { color: var(--text-muted); font-size: 13px; }
.code-block {
  margin: 0; padding: 14px 16px; border-radius: 10px;
  background: var(--bg-code, #1e1e23); color: var(--text-code, #e6e6ea);
  font-family: var(--font-mono, monospace); font-size: 12.5px; line-height: 1.7;
  white-space: pre; overflow-x: auto;
}
.code-block.tree { line-height: 1.6; }
.plex-note {
  margin-top: 14px; padding: 12px 14px; border-radius: 10px;
  border: 1px solid var(--success, #2bb673); background: var(--success-soft, rgba(43,182,115,.08));
}
.plex-note-title { display: flex; align-items: center; gap: 6px; font-weight: 600; font-size: 13px; color: var(--success, #2bb673); margin-bottom: 8px; }
.plex-note ul { margin: 0; padding-left: 18px; display: flex; flex-direction: column; gap: 7px; font-size: 13px; color: var(--text); }
.plex-note li.warn { display: flex; align-items: center; gap: 5px; color: var(--text-muted); }
</style>
