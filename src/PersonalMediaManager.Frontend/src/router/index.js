import { createRouter, createWebHistory } from 'vue-router';
import { useAuthStore } from '@/stores/auth';

const routes = [
  { path: '/setup', name: 'Setup', component: () => import('@/views/Setup.vue'), meta: { anonymous: true } },
  { path: '/login', name: 'Login', component: () => import('@/views/Login.vue'), meta: { anonymous: true } },
  {
    path: '/',
    component: () => import('@/layouts/MainLayout.vue'),
    children: [
      { path: '', name: 'Dashboard', component: () => import('@/views/DashboardPlus.vue') },
      { path: 'review', name: 'Review', component: () => import('@/views/Review.vue') },
      { path: 'pending', name: 'Pending', component: () => import('@/views/Pending.vue') },
      { path: 'history', name: 'History', component: () => import('@/views/History.vue') },
      { path: 'library', name: 'Library', component: () => import('@/views/Library.vue') },
      { path: 'library/:tmdbId', name: 'LibraryDetail', component: () => import('@/views/LibraryDetail.vue'), props: true },
      { path: 'statistics', name: 'Statistics', component: () => import('@/views/Statistics.vue') },
      { path: 'logs', name: 'Logs', component: () => import('@/views/Logs.vue') },
      { path: 'media/:id', name: 'MediaDetail', component: () => import('@/views/MediaDetail.vue'), props: true },
      // 设置（侧边栏二级）
      { path: 'settings/general', name: 'SettingsGeneral', component: () => import('@/views/settings/General.vue') },
      { path: 'settings/watch-folders', name: 'SettingsWatchFolders', component: () => import('@/views/settings/WatchFolders.vue') },
      { path: 'settings/categories', name: 'SettingsCategories', component: () => import('@/views/settings/Categories.vue') },
      { path: 'settings/archive-naming', name: 'SettingsArchiveNaming', component: () => import('@/views/settings/ArchiveNaming.vue') },
      { path: 'settings/parse-rules', name: 'SettingsParseRules', component: () => import('@/views/settings/ParseRules.vue') },
      { path: 'settings/forced-match', name: 'SettingsForcedMatch', component: () => import('@/views/settings/ForcedMatch.vue') },
      { path: 'settings/parse-testcases', name: 'SettingsParseTestCases', component: () => import('@/views/settings/ParseTestCases.vue') },
      { path: 'settings/parse-ai-providers', name: 'SettingsAiProviders', component: () => import('@/views/settings/AiProviders.vue') },
      { path: 'settings/parse-ai-providers/:id', name: 'AiMonitor', component: () => import('@/views/settings/AiMonitor.vue'), props: true },
      { path: 'settings/tmdb', name: 'SettingsTmdb', component: () => import('@/views/settings/Tmdb.vue') },
      { path: 'settings/subtitles', name: 'SettingsSubtitles', component: () => import('@/views/settings/Subtitles.vue') },
      { path: 'settings/ignore-rules', name: 'SettingsIgnoreRules', component: () => import('@/views/settings/IgnoreRules.vue') },
      { path: 'settings/media-extensions', name: 'SettingsMediaExtensions', component: () => import('@/views/settings/MediaExtensions.vue') },
      { path: 'settings/webhooks', name: 'SettingsWebhooks', component: () => import('@/views/settings/Webhooks.vue') },
      { path: 'settings/webhooks/:id/deliveries', name: 'WebhookDeliveries', component: () => import('@/views/settings/WebhookDeliveries.vue'), props: true },
      { path: 'settings/categories/:id/rules', name: 'CategoryRuleBuilder', component: () => import('@/views/settings/CategoryRuleBuilder.vue'), props: true },
      { path: 'settings/account', name: 'SettingsAccount', component: () => import('@/views/settings/Account.vue') },
      { path: 'settings/system', name: 'SettingsSystem', component: () => import('@/views/settings/SystemModule.vue') },
      { path: 'settings/update', name: 'SettingsUpdate', component: () => import('@/views/settings/UpdateSettings.vue') },
      { path: 'settings/proxy', name: 'SettingsProxy', component: () => import('@/views/settings/Proxy.vue') },
    ],
  },
  { path: '/:pathMatch(.*)*', redirect: '/' },
];

const router = createRouter({
  history: createWebHistory(),
  routes,
});

/**
 * 路由守卫（E3.3 + E3.18）：
 * 1. setup 未完成 → 强制 /setup（除非已在 /setup）
 * 2. setup 已完成但未登录 → 跳 /login（白名单 anonymous 路由放行）
 * 3. 普通路由进入前先 fetchMe 一次保证 isLoggedIn 状态准确
 */
router.beforeEach(async (to) => {
  const auth = useAuthStore();

  if (auth.setupCompleted === null) {
    await auth.checkSetupStatus();
  }
  if (!auth.setupCompleted) {
    return to.name === 'Setup' ? true : { name: 'Setup' };
  }
  if (to.name === 'Setup') {
    return { name: 'Dashboard' };
  }
  if (to.meta.anonymous) return true;

  if (!auth.isLoggedIn) {
    await auth.fetchMe();
  }
  if (!auth.isLoggedIn) {
    return { name: 'Login' };
  }
  return true;
});

export default router;
