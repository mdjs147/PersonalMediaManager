<!-- 设计稿统一页眉：eyebrow + h1 + 可选副标题 + 右侧操作槽 -->
<script setup>
defineProps({
  eyebrow: { type: String, default: '' },
  title: { type: String, required: true },
  subtitle: { type: String, default: '' },
});
</script>

<template>
  <div class="pmm-page-head">
    <div class="title-col">
      <span v-if="eyebrow" class="eyebrow">{{ eyebrow }}</span>
      <h1 class="h1">{{ title }}</h1>
      <p v-if="subtitle" class="muted subtitle">{{ subtitle }}</p>
    </div>
    <div class="actions-col">
      <slot name="actions" />
    </div>
  </div>
</template>

<style scoped lang="scss">
.pmm-page-head {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 18px;
  margin-bottom: 20px;
  flex-wrap: wrap;
}
.title-col {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}
.title-col .h1 {
  font-size: 26px;
}
.subtitle {
  margin: 4px 0 0;
  font-size: 13px;
  max-width: 720px;
}
.actions-col {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
</style>
