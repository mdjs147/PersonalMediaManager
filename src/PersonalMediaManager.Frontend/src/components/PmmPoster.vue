<!-- 海报占位：由标题哈希出渐变 + 装饰图案，与设计稿 Poster 组件等价。
     无外部图源时给海报列/卡片用，避免空白。 -->
<script setup>
import { computed, ref } from 'vue';

const props = defineProps({
  title: { type: String, default: '?' },
  year: { type: [Number, String], default: '' },
  type: { type: String, default: '' }, // movie/tv
  size: { type: String, default: 'md' }, // xs/sm/md/lg
  showText: { type: Boolean, default: true },
  src: { type: String, default: '' }, // 真实海报 URL；为空或加载失败时回退到占位渲染
});

// 真实海报加载失败 → 回退占位（渐变 + motif），避免破图
const imgFailed = ref(false);
const showImage = computed(() => !!props.src && !imgFailed.value);

function hash(s) {
  let h = 0;
  for (let i = 0; i < s.length; i++) {
    h = ((h << 5) - h + s.charCodeAt(i)) | 0;
  }
  return Math.abs(h);
}

// 设计稿 paletteFor 的简化版：5 套配色循环 + hash 角度
const PALETTES = [
  ['#1e3a5f', '#3b6ea5', '#f5b400'],
  ['#3d1c4f', '#7d2b86', '#e94e77'],
  ['#1e4d3b', '#3f7a5b', '#a8d44c'],
  ['#4d2a1a', '#a55a32', '#f4c542'],
  ['#1a2a4d', '#3c5fa5', '#e8eef7'],
  ['#4d1f29', '#a53c4f', '#f4a4b4'],
];

const h = computed(() => hash(props.title));
const palette = computed(() => PALETTES[h.value % PALETTES.length]);
const angle = computed(() => (h.value % 90) + 30);

const sizes = {
  xs: { w: 36, fs: 9, sub: 7 },
  sm: { w: 60, fs: 11, sub: 8 },
  md: { w: 100, fs: 13, sub: 10 },
  lg: { w: 140, fs: 16, sub: 11 },
};
const dim = computed(() => sizes[props.size] || sizes.md);

const motifs = ['circle', 'ring', 'arc', 'lines', 'triangle'];
const motif = computed(() => motifs[h.value % motifs.length]);

const bgStyle = computed(() => ({
  background: `linear-gradient(${angle.value}deg, ${palette.value[0]} 0%, ${palette.value[1]} 60%, ${palette.value[2]} 130%)`,
}));
</script>

<template>
  <div class="poster poster-grain" :style="{ width: dim.w + 'px' }">
    <div class="bg-layer" :style="bgStyle" />
    <svg viewBox="0 0 100 150" preserveAspectRatio="none" class="motif">
      <circle
        v-if="motif === 'circle'"
        :cx="(h % 80) + 10"
        :cy="((h >> 3) % 60) + 30"
        :r="(h % 30) + 15"
        :fill="palette[2]"
        opacity="0.6"
      />
      <circle
        v-else-if="motif === 'ring'"
        cx="50"
        cy="60"
        :r="(h % 30) + 25"
        fill="none"
        :stroke="palette[2]"
        stroke-width="2"
        opacity="0.55"
      />
      <path
        v-else-if="motif === 'arc'"
        :d="`M 0 ${80 + (h % 30)} Q 50 ${20 + (h % 30)} 100 ${80 + (h % 30)}`"
        fill="none"
        :stroke="palette[2]"
        stroke-width="2"
        opacity="0.5"
      />
      <g v-else-if="motif === 'lines'">
        <line
          v-for="i in 6"
          :key="i"
          x1="-10"
          :y1="20 + (i - 1) * 22"
          x2="110"
          :y2="5 + (i - 1) * 22"
          :stroke="palette[2]"
          stroke-width="1"
          opacity="0.4"
        />
      </g>
      <polygon
        v-else-if="motif === 'triangle'"
        :points="`50,${10 + (h % 20)} 90,${100 - (h % 20)} 10,${100 - (h % 20)}`"
        :fill="palette[2]"
        opacity="0.45"
      />
    </svg>
    <div v-if="type && size !== 'xs' && size !== 'sm'" class="type-chip">
      {{ type === 'movie' ? 'MOVIE' : 'TV' }}
    </div>
    <div v-if="showText && size !== 'xs'" class="poster-inner">
      <div class="poster-title font-display" :style="{ fontSize: dim.fs + 'px' }">{{ title }}</div>
      <div v-if="year" class="poster-sub" :style="{ fontSize: dim.sub + 'px' }">{{ year }}</div>
    </div>
    <img
      v-if="showImage"
      :src="src"
      class="real-poster"
      loading="lazy"
      alt=""
      @error="imgFailed = true"
    />
  </div>
</template>

<style scoped lang="scss">
.poster {
  position: relative;
  aspect-ratio: 2 / 3;
  border-radius: var(--r-2);
  overflow: hidden;
  background: var(--surface-2);
  display: block;
  flex-shrink: 0;
  box-shadow: var(--shadow-poster);
  border: 1px solid var(--border-soft);
}
.bg-layer {
  position: absolute;
  inset: 0;
}
.motif {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  opacity: 0.35;
}
.poster-grain::after {
  content: '';
  position: absolute;
  inset: 0;
  background:
    radial-gradient(120% 120% at 60% 20%, rgba(255, 255, 255, 0.18), transparent 50%),
    linear-gradient(to bottom, transparent 30%, rgba(0, 0, 0, 0.55) 95%);
  pointer-events: none;
}
.type-chip {
  position: absolute;
  top: 8px;
  left: 8px;
  padding: 2px 6px;
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  background: rgba(0, 0, 0, 0.45);
  color: white;
  border-radius: 3px;
  z-index: 2;
}
.poster-inner {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  justify-content: flex-end;
  padding: 10px;
  color: white;
  z-index: 1;
}
.poster-title {
  font-family: 'Outfit', sans-serif;
  font-weight: 700;
  line-height: 1.15;
  text-shadow: 0 1px 4px rgba(0, 0, 0, 0.6);
}
.poster-sub {
  opacity: 0.8;
  margin-top: 2px;
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.6);
}
.real-poster {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: cover;
  z-index: 3;
}
</style>
