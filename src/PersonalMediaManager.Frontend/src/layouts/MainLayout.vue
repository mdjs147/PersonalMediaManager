<script setup>
import { computed, ref, onMounted, onBeforeUnmount, watch } from 'vue';
import { RouterLink, RouterView, useRouter, useRoute } from 'vue-router';
import { ElMessageBox, ElDropdown, ElDropdownMenu, ElDropdownItem, ElNotification } from 'element-plus';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { api } from '@/api';
import { tokenStorage } from '@/api/http';
import { useAuthStore } from '@/stores/auth';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmBrandMark from '@/components/PmmBrandMark.vue';
import AboutDialog from '@/components/AboutDialog.vue';
import GlobalSearch from '@/components/GlobalSearch.vue';

// 主版本号来自 vite.config define（构建期烤进 bundle）；运行时实际值由关于对话框调 /system/version 再次拉取
const productVersion = __APP_PRODUCT_VERSION__;
const aboutVisible = ref(false);

const auth = useAuthStore();
const router = useRouter();
const route = useRoute();

// 后端所有扫描入口最终共用处理管线；文件进入 AwaitingReview 时，统一由 /hubs/tasks 推送状态事件。
// 在布局层订阅可覆盖手动全扫、周期/实时自动扫描与单目录扫描，不依赖用户停留在哪个页面。
const REVIEW_NOTIFICATION_SETTING_KEY = 'Notification_ReviewRequiredEnabled';
const reviewNotificationEnabled = ref(false);
let taskConnection = null;

function parseBooleanSetting(value) {
  return String(value).toLowerCase() === 'true';
}

async function loadReviewNotificationSetting() {
  try {
    const data = (await api.generalSettings.get()) || {};
    const setting = (data.groups?.General || []).find((item) => item.key === REVIEW_NOTIFICATION_SETTING_KEY);
    reviewNotificationEnabled.value = parseBooleanSetting(setting?.value);
  } catch {
    // 设置未能确认时保持关闭，避免违背用户已关闭提醒的选择；下次刷新页面会重新读取。
    reviewNotificationEnabled.value = false;
  }
}

function onGeneralSettingsUpdated(event) {
  const setting = (event.detail?.items || []).find((item) => item.key === REVIEW_NOTIFICATION_SETTING_KEY);
  if (setting) reviewNotificationEnabled.value = parseBooleanSetting(setting.value);
}

function onTaskStatusChanged(event) {
  if (!reviewNotificationEnabled.value || event?.newStatus !== 'AwaitingReview') return;

  ElNotification({
    title: '发现待确认文件',
    message: `「${event.fileName || '未命名文件'}」需要人工确认，点击前往待确认队列。`,
    type: 'warning',
    position: 'bottom-right',
    duration: 10000,
    onClick: () => router.push('/review'),
  });
}

async function connectTaskNotifications() {
  await loadReviewNotificationSetting();
  taskConnection = new HubConnectionBuilder()
    .withUrl('/hubs/tasks', { accessTokenFactory: () => tokenStorage.get() || '' })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
  taskConnection.on('taskStatusChanged', onTaskStatusChanged);
  try {
    await taskConnection.start();
  } catch {
    // 实时提醒是辅助能力，连接失败不能影响页面使用；刷新页面时会重新连接。
  }
}

onMounted(() => {
  window.addEventListener('pmm:general-settings-updated', onGeneralSettingsUpdated);
  connectTaskNotifications();
});
onBeforeUnmount(async () => {
  window.removeEventListener('pmm:general-settings-updated', onGeneralSettingsUpdated);
  try {
    await taskConnection?.stop();
  } catch {
    /* 页面卸载时连接可能已断开，无需干扰退出流程 */
  }
});

// 主题切换：dark/light，落地到 <html data-theme="...">
const theme = ref(localStorage.getItem('pmm-theme') || 'dark');
onMounted(() => {
  document.documentElement.setAttribute('data-theme', theme.value);
});
watch(theme, (v) => {
  document.documentElement.setAttribute('data-theme', v);
  localStorage.setItem('pmm-theme', v);
});
function toggleTheme() {
  theme.value = theme.value === 'dark' ? 'light' : 'dark';
}

// 全局搜索框：顶栏快捷键（Ctrl/Cmd+K，或非输入态按 /）聚焦
const searchRef = ref(null);
function isEditableTarget(el) {
  if (!el) return false;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
}
function onGlobalKeydown(e) {
  const isCmdK = (e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K');
  const isSlash = e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && !isEditableTarget(e.target);
  if (isCmdK || isSlash) {
    e.preventDefault();
    searchRef.value?.focus();
  }
}
onMounted(() => window.addEventListener('keydown', onGlobalKeydown));
onBeforeUnmount(() => window.removeEventListener('keydown', onGlobalKeydown));

// 侧栏分组：概览 + 设置按语义拆 4 组（采集 / 识别归类 / 集成 / 系统）
const NAV_GROUPS = [
  {
    label: '概览',
    items: [
      { key: '/', label: '仪表盘', icon: 'dashboard' },
      { key: '/library', label: '媒体库', icon: 'film' },
      { key: '/statistics', label: '统计分析', icon: 'chart' },
      { key: '/review', label: '待确认队列', icon: 'inbox' },
      { key: '/pending', label: '处理队列', icon: 'layers' },
      { key: '/history', label: '处理历史', icon: 'history' },
      { key: '/logs', label: '实时日志', icon: 'terminal' },
    ],
  },
  {
    // 采集：决定「哪些文件会被纳入处理」的三件套
    label: '采集',
    items: [
      { key: '/settings/watch-folders', label: '监控目录', icon: 'folder' },
      { key: '/settings/media-extensions', label: '媒体格式', icon: 'film' },
      { key: '/settings/ignore-rules', label: '忽略规则', icon: 'ban' },
    ],
  },
  {
    // 识别归类：解析 → 兜底 → 匹配元数据 → 落库分类（媒体分类为管道末步）
    label: '识别归类',
    items: [
      { key: '/settings/parse-rules', label: '解析规则', icon: 'filter' },
      { key: '/settings/forced-match', label: '强制匹配', icon: 'shield' },
      { key: '/settings/parse-testcases', label: '测试集', icon: 'check' },
      { key: '/settings/parse-ai-providers', label: 'AI 提供商', icon: 'brain' },
      { key: '/settings/tmdb', label: 'TMDB', icon: 'globe' },
      { key: '/settings/subtitles', label: '字幕', icon: 'download' },
      { key: '/settings/categories', label: '媒体分类', icon: 'layers' },
      { key: '/settings/archive-naming', label: '归档命名', icon: 'edit' },
    ],
  },
  {
    // 集成：出站对外（Webhook 推送 + 出站代理）
    label: '集成',
    items: [
      { key: '/settings/webhooks', label: 'Webhook', icon: 'webhook' },
      { key: '/settings/proxy', label: '代理', icon: 'link' },
    ],
  },
  {
    label: '系统',
    items: [
      { key: '/settings/general', label: '常规', icon: 'settings' },
      { key: '/settings/account', label: '账户', icon: 'user' },
      { key: '/settings/system', label: '系统', icon: 'server' },
      { key: '/settings/update', label: '软件更新', icon: 'download' },
    ],
  },
];

// 当前激活的导航 key（path 完全匹配；首页 / 单独匹配）
function isActive(itemKey) {
  if (itemKey === '/') return route.path === '/' || route.path === '';
  return route.path === itemKey || route.path.startsWith(itemKey + '/');
}

// 面包屑：按导航项推导
const crumbs = computed(() => {
  for (const g of NAV_GROUPS) {
    for (const it of g.items) {
      if (isActive(it.key)) return ['首页', it.label];
    }
  }
  return ['首页'];
});

const avatarChar = computed(() => (auth.user?.username?.[0] || 'A').toUpperCase());

async function onLogout() {
  await ElMessageBox.confirm('确认退出登录？', '提示', { type: 'warning' });
  await auth.logout();
  router.push({ name: 'Login' });
}
</script>

<template>
  <div class="app">
    <!-- 侧栏 -->
    <aside class="sidebar">
      <div class="sidebar-brand">
        <PmmBrandMark class="brand-mark" :size="28" :show-status-dot="false" />
        <div class="brand-name">PersonalMedia<span>Manager</span></div>
      </div>

      <nav class="sidebar-nav">
        <template v-for="g in NAV_GROUPS" :key="g.label">
          <div class="sidebar-group-label">{{ g.label }}</div>
          <!--
            用 RouterLink 而非 <a href="#" @click.prevent>：
            前者渲染为带真实 href 的 a 标签，支持中键 / Ctrl+点击新标签打开、右键复制链接、辅助技术读取目标，
            且 SPA 内部导航仍由 vue-router 接管（不会刷新整页）。
            active 状态保留自定义 isActive：默认的 active-class 只能严格匹配，无法处理首页 / 与子路径前缀。
          -->
          <RouterLink
            v-for="item in g.items"
            :key="item.key"
            :to="item.key"
            class="nav-item"
            :class="{ active: isActive(item.key) }"
            active-class=""
            exact-active-class=""
          >
            <PmmIcon :name="item.icon" :size="17" />
            <span>{{ item.label }}</span>
          </RouterLink>
        </template>
      </nav>

      <button class="sidebar-footer" type="button" title="查看版本详情" @click="aboutVisible = true">
        <div class="svc-dot" />
        <div class="svc-text">
          服务运行中
          <span class="muted">v{{ productVersion }}</span>
        </div>
      </button>
    </aside>

    <AboutDialog v-model="aboutVisible" />

    <!-- 主列 -->
    <div class="main-col">
      <header class="topbar">
        <div class="crumbs">
          <template v-for="(c, i) in crumbs" :key="i">
            <span v-if="i > 0" class="sep">/</span>
            <span :class="{ last: i === crumbs.length - 1 }">{{ c }}</span>
          </template>
        </div>

        <GlobalSearch ref="searchRef" />

        <button class="icon-btn" :title="theme === 'dark' ? '切换为浅色' : '切换为深色'" @click="toggleTheme">
          <PmmIcon :name="theme === 'dark' ? 'sun' : 'moon'" :size="18" />
        </button>

        <el-dropdown trigger="click">
          <div class="avatar" :title="auth.user?.username || 'admin'">{{ avatarChar }}</div>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item @click="router.push('/settings/account')">账户设置</el-dropdown-item>
              <el-dropdown-item divided @click="onLogout">退出登录</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </header>

      <main class="page-scroll">
        <RouterView />
      </main>
    </div>
  </div>
</template>
