import { defineStore } from 'pinia';
import { api } from '@/api';
import { tokenStorage } from '@/api/http';

export const useAuthStore = defineStore('auth', {
  state: () => ({
    user: null,           // { id, username, role }
    isLoggedIn: false,
    isCheckingSetup: false,
    setupCompleted: null, // null=未检测 / true / false
  }),
  actions: {
    async checkSetupStatus() {
      this.isCheckingSetup = true;
      try {
        const data = await api.setup.status();
        this.setupCompleted = !!data?.isCompleted;
        return this.setupCompleted;
      } finally {
        this.isCheckingSetup = false;
      }
    },
    async login(username, password) {
      // 后端 LoginResponse = { token, user: { id, username, role, lastLoginAt } }
      const data = await api.auth.login({ username, password });
      tokenStorage.set(data.token);
      this.user = { id: data.user.id, username: data.user.username, role: data.user.role };
      this.isLoggedIn = true;
      return data;
    },
    async fetchMe() {
      try {
        // 后端 GET /api/auth/me 返回 UserSummary = { id, username, role, lastLoginAt }
        const data = await api.auth.me();
        this.user = { id: data.id, username: data.username, role: data.role };
        this.isLoggedIn = true;
        return data;
      } catch {
        this.isLoggedIn = false;
        this.user = null;
        return null;
      }
    },
    async logout() {
      try { await api.auth.logout(); } catch { /* 即使后端失败也强制清本地 */ }
      tokenStorage.clear();
      this.user = null;
      this.isLoggedIn = false;
      // r1 P3-12：logout 后重置 setupCompleted=null（不立刻请求），
      // 路由守卫下次进入时若仍为 null 会自动 checkSetupStatus；
      // 兼顾「数据库被 import 替换」「后端 setup 被外部重置」等边角场景。
      this.setupCompleted = null;
    },
  },
});
