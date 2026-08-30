<!--
  PmmBrandMark — Tray Icon D 同源 chevron mark
  与 handoff 包 tray-icon-d.jsx 的 ChevronMark 几何 1:1：
    · 32×32 master viewBox
    · 琥珀方块 + Plex 风格双 chevron + 右下角状态点
    · size ≤ 22 自动切低密度（单 chevron + 加大状态点 + crispEdges）
-->
<script setup>
import { computed } from 'vue';

const props = defineProps({
  /** 渲染像素尺寸（master = 32） */
  size: { type: [Number, String], default: 32 },
  /** 状态：running / stopped / paused / syncing / error */
  state: { type: String, default: 'running' },
  /** 渲染模式：color（默认彩色）/ template-dark / template-light */
  mode: { type: String, default: 'color' },
  /** 是否渲染右下角状态点（favicon / 大尺寸品牌位可关掉） */
  showStatusDot: { type: Boolean, default: true },
});

// 与设计稿 tray-icon-d.jsx 顶部常量同源
const PALETTE = {
  amber: '#e5a00d',
  amberDeep: '#15110a',
  run: '#4ade80',
  stop: '#ef6b6b',
  pause: '#fbbf24',
  sync: '#60a5fa',
  err: '#ef6b6b',
};

const tiny = computed(() => Number(props.size) <= 22);

const visual = computed(() => {
  const isOutline = props.state === 'stopped' || props.state === 'error';
  switch (props.state) {
    case 'running':
      return { bodyFill: PALETTE.amber, chevron: PALETTE.amberDeep, dot: PALETTE.run, isOutline };
    case 'stopped':
      return { bodyFill: 'transparent', stroke: '#6b7383', chevron: '#6b7383', dot: PALETTE.stop, isOutline };
    case 'paused':
      return { bodyFill: PALETTE.amber, chevron: PALETTE.amberDeep, dot: PALETTE.pause, isOutline };
    case 'syncing':
      return { bodyFill: PALETTE.amber, chevron: PALETTE.amberDeep, dot: PALETTE.sync, isOutline };
    case 'error':
      return { bodyFill: 'transparent', stroke: PALETTE.err, chevron: PALETTE.err, dot: PALETTE.err, isOutline };
    default:
      return { bodyFill: PALETTE.amber, chevron: PALETTE.amberDeep, dot: PALETTE.run, isOutline: false };
  }
});

// 几何参数：完全镜像 jsx 实现
const geom = computed(() => ({
  r: tiny.value ? 5 : 6.5,
  sw: tiny.value ? 3 : 2.8,
  chevronD1: tiny.value ? 'M11 9 L19 16 L11 23' : 'M9.5 10 L16 16 L9.5 22',
  chevronD2: 'M16.5 10 L23 16 L16.5 22',
  dotR: tiny.value ? 5.5 : 4.2,
  dotCx: tiny.value ? 26 : 27,
  dotCy: tiny.value ? 26 : 27,
  dotStrokeW: tiny.value ? 2 : 1.6,
}));

const isTemplate = computed(() => props.mode === 'template-dark' || props.mode === 'template-light');
const templateMain = computed(() => (props.mode === 'template-dark' ? '#ffffff' : '#1c1c1e'));
const templateCutout = computed(() => (props.mode === 'template-dark' ? '#000000' : '#ffffff'));
const isPaused = computed(() => props.state === 'paused');
const isSyncing = computed(() => props.state === 'syncing');
const shapeRendering = computed(() => (tiny.value ? 'crispEdges' : 'geometricPrecision'));
</script>

<template>
  <svg
    xmlns="http://www.w3.org/2000/svg"
    :width="size"
    :height="size"
    viewBox="0 0 32 32"
    :shape-rendering="shapeRendering"
    aria-label="PersonalMediaManager"
    role="img"
  >
    <!-- TEMPLATE 模式（macOS template image / 单色剪影） -->
    <template v-if="isTemplate">
      <template v-if="state === 'running' || state === 'syncing'">
        <rect x="2" y="2" width="28" height="28" :rx="tiny ? 5 : 6.5" :fill="templateMain" />
        <path :d="geom.chevronD1" fill="none" :stroke="templateCutout" :stroke-width="geom.sw" stroke-linecap="round" stroke-linejoin="round" />
        <path v-if="!tiny" :d="geom.chevronD2" fill="none" :stroke="templateCutout" :stroke-width="geom.sw" stroke-linecap="round" stroke-linejoin="round" opacity="0.45" />
      </template>
      <template v-else-if="state === 'stopped'">
        <rect x="2.6" y="2.6" width="26.8" height="26.8" :rx="tiny ? 5 : 6.2" fill="none" :stroke="templateMain" :stroke-width="tiny ? 2.2 : 1.8" />
        <path :d="geom.chevronD1" fill="none" :stroke="templateMain" :stroke-width="tiny ? 2.6 : 2.4" stroke-linecap="round" stroke-linejoin="round" />
        <path v-if="!tiny" :d="geom.chevronD2" fill="none" :stroke="templateMain" :stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" opacity="0.4" />
      </template>
      <template v-else-if="state === 'paused'">
        <rect x="2" y="2" width="28" height="28" :rx="tiny ? 5 : 6.5" :fill="templateMain" />
        <rect x="10" y="9" width="4" height="14" rx="1.2" :fill="templateCutout" />
        <rect x="18" y="9" width="4" height="14" rx="1.2" :fill="templateCutout" />
      </template>
      <template v-else-if="state === 'error'">
        <rect x="2.6" y="2.6" width="26.8" height="26.8" :rx="tiny ? 5 : 6.2" fill="none" :stroke="templateMain" :stroke-width="tiny ? 2.2 : 1.8" />
        <rect x="14.5" y="8" width="3" height="9" rx="1.2" :fill="templateMain" />
        <rect x="14.5" y="20" width="3" height="3" rx="1.2" :fill="templateMain" />
      </template>
    </template>

    <!-- FULL COLOR 模式 -->
    <template v-else>
      <!-- 主体方块 -->
      <rect
        v-if="visual.isOutline"
        x="2.6"
        y="2.6"
        width="26.8"
        height="26.8"
        :rx="geom.r - 0.3"
        fill="none"
        :stroke="visual.stroke"
        :stroke-width="tiny ? 2.2 : 1.8"
      />
      <rect
        v-else
        x="2"
        y="2"
        width="28"
        height="28"
        :rx="geom.r"
        :fill="visual.bodyFill"
      />

      <!-- Glyph：pause 双竖条 / 其他 chevron -->
      <template v-if="isPaused">
        <rect x="10" y="9" width="4" height="14" rx="1.2" :fill="visual.chevron" />
        <rect x="18" y="9" width="4" height="14" rx="1.2" :fill="visual.chevron" />
      </template>
      <g v-else :class="{ 'pmm-brand-spin': isSyncing }">
        <path :d="geom.chevronD1" fill="none" :stroke="visual.chevron" :stroke-width="geom.sw" stroke-linecap="round" stroke-linejoin="round" />
        <path v-if="!tiny" :d="geom.chevronD2" fill="none" :stroke="visual.chevron" :stroke-width="geom.sw" stroke-linecap="round" stroke-linejoin="round" opacity="0.45" />
      </g>

      <!-- 状态点 -->
      <circle
        v-if="showStatusDot"
        :cx="geom.dotCx"
        :cy="geom.dotCy"
        :r="geom.dotR"
        :fill="visual.dot"
        stroke="#0a0e14"
        :stroke-width="geom.dotStrokeW"
      />

      <!-- 填充体内顶部高光（≥24px 才加） -->
      <rect
        v-if="!visual.isOutline && !tiny"
        x="2"
        y="2"
        width="28"
        height="14"
        :rx="geom.r"
        fill="url(#pmm-brand-shine)"
        opacity="0.18"
      />

      <defs>
        <linearGradient id="pmm-brand-shine" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#fff" />
          <stop offset="100%" stop-color="#fff" stop-opacity="0" />
        </linearGradient>
      </defs>
    </template>
  </svg>
</template>

<style scoped>
.pmm-brand-spin {
  transform-origin: 16px 16px;
  animation: pmm-brand-spin 1.4s linear infinite;
}
@keyframes pmm-brand-spin {
  to { transform: rotate(360deg); }
}
</style>
