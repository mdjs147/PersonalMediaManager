<script setup>
/**
 * TMDB 图片（走后端代理 + 占位降级）
 * 用于背景图 / 人物照 / 剧照 / Logo —— 浏览器不直连 TMDB CDN，统一经 /api/library/tmdb-image 代理。
 */
import { computed, ref, watch } from 'vue';
import { api } from '@/api';

const props = defineProps({
  path: { type: String, default: '' },
  size: { type: String, default: 'w185' },
  alt: { type: String, default: '' },
});

const failed = ref(false);
watch(
  () => props.path,
  () => {
    failed.value = false;
  },
);
const src = computed(() => (props.path && !failed.value ? api.library.imageUrl(props.size, props.path) : ''));
</script>

<template>
  <img v-if="src" :src="src" :alt="alt" class="tmdb-img" loading="lazy" @error="failed = true" />
  <div v-else class="tmdb-img tmdb-img-ph"><slot /></div>
</template>

<style scoped>
.tmdb-img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.tmdb-img-ph {
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--surface-1, #1a2230);
  color: var(--text-dim, #889);
  font-size: 11px;
}
</style>
