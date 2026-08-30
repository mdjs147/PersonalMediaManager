<script setup>
import { ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useAuthStore } from '@/stores/auth';
import PmmBrandMark from '@/components/PmmBrandMark.vue';

// 主版本号来自 vite.config define 注入（构建期烤进 bundle）；登录前没法调 API，必须用构建期常量
const productVersion = __APP_PRODUCT_VERSION__;

const router = useRouter();
const route = useRoute();
const auth = useAuthStore();
const submitting = ref(false);
const form = ref({ username: '', password: '' });

async function onSubmit() {
  submitting.value = true;
  try {
    await auth.login(form.value.username, form.value.password);
    // 401 拦截器会把原路径编码进 ?redirect=...；登录成功后回跳
    const redirect = route.query.redirect;
    if (redirect && typeof redirect === 'string') {
      router.replace(decodeURIComponent(redirect));
    } else {
      router.replace('/');
    }
  } finally {
    submitting.value = false;
  }
}
</script>

<template>
  <div class="auth-screen">
    <div class="auth-glow" aria-hidden="true" />
    <div class="auth-card card">
      <div class="auth-brand">
        <PmmBrandMark class="brand-mark" :size="32" :show-status-dot="false" />
        <div class="brand-name">PersonalMedia<span>Manager</span></div>
      </div>
      <div class="auth-title">
        <h1 class="h1">欢迎回来</h1>
        <p class="muted">登录以继续管理你的媒体库</p>
      </div>
      <el-form label-position="top" class="auth-form" @submit.prevent="onSubmit">
        <el-form-item label="账号">
          <el-input v-model="form.username" autofocus placeholder="用户名" />
        </el-form-item>
        <el-form-item label="密码">
          <el-input
            v-model="form.password"
            type="password"
            show-password
            placeholder="••••••••"
            @keyup.enter="onSubmit"
          />
        </el-form-item>
        <el-button type="primary" :loading="submitting" class="auth-submit" @click="onSubmit">
          登录
        </el-button>
      </el-form>
      <div class="auth-foot dim">v{{ productVersion }} · Personal Media Manager</div>
    </div>
  </div>
</template>

<style scoped lang="scss">
.auth-screen {
  position: relative;
  min-height: 100vh;
  display: grid;
  place-items: center;
  background:
    radial-gradient(900px 500px at 20% -100px, var(--accent-soft), transparent),
    radial-gradient(700px 500px at 110% 110%, color-mix(in oklab, var(--accent) 18%, transparent), transparent),
    var(--bg);
  overflow: hidden;
}
.auth-glow {
  position: absolute;
  inset: 0;
  background:
    radial-gradient(circle at 30% 40%, rgba(229, 160, 13, 0.08), transparent 40%),
    radial-gradient(circle at 70% 60%, rgba(96, 165, 250, 0.05), transparent 45%);
  pointer-events: none;
}
.auth-card {
  position: relative;
  width: min(420px, 92vw);
  padding: 32px 30px 26px;
  background: var(--bg-elev);
  border: 1px solid var(--border);
  border-radius: var(--r-4);
  box-shadow: var(--shadow-3);
  display: flex;
  flex-direction: column;
  gap: 22px;
}
.auth-brand {
  display: flex;
  align-items: center;
  gap: 10px;
}
.auth-title h1 {
  margin: 0;
  font-size: 26px;
}
.auth-title p {
  margin: 6px 0 0;
  font-size: 13px;
}
.auth-form :deep(.el-form-item) {
  margin-bottom: 14px;
}
.auth-submit {
  width: 100%;
  height: 40px;
  font-weight: 700;
  letter-spacing: 0.02em;
}
.auth-foot {
  text-align: center;
  font-size: 12px;
  letter-spacing: 0.04em;
  border-top: 1px solid var(--border-soft);
  padding-top: 14px;
}
</style>
