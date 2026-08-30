/**
 * 公共格式化工具：时间 / 大小，被多个高保真页面共用
 */

export function timeAgo(iso) {
  if (!iso) return '';
  const d = (Date.now() - new Date(iso).getTime()) / 1000;
  if (d < 0) return '即将';
  if (d < 60) return Math.floor(d) + ' 秒前';
  if (d < 3600) return Math.floor(d / 60) + ' 分钟前';
  if (d < 86400) return Math.floor(d / 3600) + ' 小时前';
  return Math.floor(d / 86400) + ' 天前';
}

export function fmtTime(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return d.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  });
}

export function fmtClock(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return d.toLocaleTimeString('zh-CN', { hour12: false });
}

export function fmtSize(b) {
  if (b == null) return '—';
  if (typeof b === 'string') return b;
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  let v = b;
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return v.toFixed(1) + ' ' + u[i];
}

// 配额数字缩写：中文万/亿单位（1.2万 / 100万 / 1.5亿），AI 套餐限额展示用
export function formatQuota(n) {
  if (n == null) return '—';
  const v = Number(n);
  if (!Number.isFinite(v)) return '—';
  const abs = Math.abs(v);
  // 缩写值 ≥100 取整（100万），否则保留 1 位小数并去掉尾零（1.2万 / 3万）
  const trim = (x) => (Math.abs(x) >= 100 ? String(Math.round(x)) : x.toFixed(1).replace(/\.0$/, ''));
  if (abs >= 1e8) return trim(v / 1e8) + '亿';
  if (abs >= 1e4) return trim(v / 1e4) + '万';
  return v.toLocaleString();
}

// 去掉文件名最后一个扩展名段（无扩展名时原样返回），字幕搜索默认关键词等场景用
export function stripExt(name) {
  return String(name || '').replace(/\.[^/.]+$/, '');
}

// 运行时长（秒）→ 中文「N 天 N 小时」紧凑格式，仪表盘服务状态用
export function fmtUptime(sec) {
  if (sec == null || sec < 0) return '—';
  const s = Math.floor(sec);
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d} 天 ${h} 小时`;
  if (h > 0) return `${h} 小时 ${m} 分`;
  if (m > 0) return `${m} 分钟`;
  return `${s} 秒`;
}

export const STATUS_MAP = {
  Detected: { label: '已发现', variant: 'neutral' },
  Queued: { label: '排队', variant: 'neutral' },
  Parsing: { label: '解析中', variant: 'info' },
  TmdbMatching: { label: 'TMDB 匹配', variant: 'info' },
  AiParsing: { label: 'AI 解析', variant: 'info' },
  TmdbRematching: { label: 'TMDB 二次', variant: 'info' },
  Classifying: { label: '分类中', variant: 'info' },
  AwaitingReview: { label: '待确认', variant: 'warning' },
  Archiving: { label: '归档中', variant: 'info' },
  Completed: { label: '已完成', variant: 'success' },
  Archived: { label: '已归档', variant: 'success' },
  Skipped: { label: '已跳过', variant: 'warning' },
  Ignored: { label: '已忽略', variant: 'neutral' },
  Cancelled: { label: '已取消', variant: 'neutral' },
  Failed: { label: '失败', variant: 'danger' },
};

export const SOURCE_MAP = {
  Rule: { label: '规则', variant: 'success', icon: 'rule' },
  // 后端 JsonStringEnumConverter 输出 PascalCase："Ai"（非全大写 "AI"）
  Ai: { label: 'AI', variant: 'accent', icon: 'brain' },
  Hybrid: { label: '混合', variant: 'info', icon: 'layers' },
  // 强制匹配（pmm.txt / TMDB URL 标识锚定，跳过规则与 AI 自主判断）
  Manual: { label: '强制匹配', variant: 'warning', icon: 'shield' },
};

export const COUNTRY_FLAGS = {
  CN: '🇨🇳',
  US: '🇺🇸',
  JP: '🇯🇵',
  KR: '🇰🇷',
  GB: '🇬🇧',
  HK: '🇭🇰',
  TW: '🇹🇼',
  FR: '🇫🇷',
  DE: '🇩🇪',
};
