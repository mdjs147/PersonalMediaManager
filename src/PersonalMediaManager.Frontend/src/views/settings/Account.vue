<script setup>
import { onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox, ElDialog } from 'element-plus';
import { api } from '@/api';
import { useAuthStore } from '@/stores/auth';
import PmmIcon from '@/components/PmmIcon.vue';
import PmmPageHeader from '@/components/PmmPageHeader.vue';

const auth = useAuthStore();
const users = ref([]);
const loading = ref(false);
const pwd = ref({ visible: false, oldPassword: '', newPassword: '', confirm: '' });
const newUser = ref({ visible: false, username: '', password: '', role: 'Viewer' });

async function load() {
  loading.value = true;
  try {
    // api.account.listUsers() 后端返回直接数组（无 Items 包装）；http.js 拦截器已 return body.data
    users.value = (await api.account.listUsers()) || [];
  } finally {
    loading.value = false;
  }
}

async function onChangePwd() {
  if (pwd.value.newPassword !== pwd.value.confirm) return ElMessage.warning('两次输入不一致');
  await api.account.changePassword({
    oldPassword: pwd.value.oldPassword,
    newPassword: pwd.value.newPassword,
  });
  ElMessage.success('密码已修改');
  pwd.value = { visible: false, oldPassword: '', newPassword: '', confirm: '' };
}

async function onCreate() {
  await api.account.createUser({
    username: newUser.value.username,
    password: newUser.value.password,
    role: newUser.value.role,
  });
  ElMessage.success('创建成功');
  newUser.value = { visible: false, username: '', password: '', role: 'Viewer' };
  load();
}

async function onDelete(u) {
  await ElMessageBox.confirm(`删除用户 ${u.username}？`, '提示', { type: 'warning' });
  await api.account.deleteUser(u.id);
  ElMessage.success('已删除');
  load();
}

function fmtTime(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('zh-CN', { hour12: false });
}

onMounted(load);
</script>

<template>
  <div class="page" v-loading="loading">
    <PmmPageHeader
      eyebrow="设置"
      title="账户"
      subtitle="管理可访问 WebUI 的用户。匿名用户可查看仪表盘、队列、历史与日志（依据常规设置）。"
    >
      <template #actions>
        <button class="btn btn-ghost btn-sm" @click="pwd.visible = true">
          <PmmIcon name="shield" :size="14" /> 修改我的密码
        </button>
        <button class="btn btn-primary btn-sm" @click="newUser.visible = true">
          <PmmIcon name="plus" :size="14" /> 新增用户
        </button>
      </template>
    </PmmPageHeader>

    <section class="section-card">
      <header class="section-head">
        <div>
          <h3>当前账户</h3>
          <div class="sub">{{ auth.user?.username }} · {{ auth.user?.role }}</div>
        </div>
      </header>
      <div class="form-row">
        <div>
          <div class="label">登录名</div>
          <div class="hint">登录名不可修改。</div>
        </div>
        <div class="control">
          <span class="font-mono" style="color: var(--text)">{{ auth.user?.username }}</span>
        </div>
      </div>
    </section>

    <section class="section-card">
      <header class="section-head">
        <h3>用户列表</h3>
      </header>
      <div class="table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th>用户名</th>
              <th>角色</th>
              <th>最近登录</th>
              <th>创建时间</th>
              <th style="width: 80px"></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="u in users" :key="u.id">
              <td>
                <div class="hstack" style="gap: 10px">
                  <div class="user-avatar">{{ u.username[0]?.toUpperCase() }}</div>
                  <span class="cell-strong">{{ u.username }}</span>
                </div>
              </td>
              <td>
                <span class="tag" :class="u.role === 'Admin' ? 'tag-accent' : 'tag-neutral'">{{ u.role }}</span>
              </td>
              <td class="muted small">{{ fmtTime(u.lastLoginAt) }}</td>
              <td class="muted small">{{ fmtTime(u.createdAt) }}</td>
              <td>
                <button
                  v-if="u.username !== auth.user?.username"
                  class="icon-btn"
                  title="删除"
                  @click="onDelete(u)"
                >
                  <PmmIcon name="trash" :size="15" />
                </button>
              </td>
            </tr>
            <tr v-if="!users.length && !loading">
              <td colspan="5" class="empty">暂无其他用户</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <!-- 修改密码 -->
    <el-dialog v-model="pwd.visible" title="修改密码" width="420px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">原密码</div>
          <input class="input" type="password" v-model="pwd.oldPassword" />
        </div>
        <div class="field">
          <div class="field-label">新密码</div>
          <input class="input" type="password" v-model="pwd.newPassword" />
        </div>
        <div class="field">
          <div class="field-label">确认新密码</div>
          <input class="input" type="password" v-model="pwd.confirm" />
        </div>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="pwd.visible = false">取消</button>
        <button class="btn btn-primary" @click="onChangePwd"><PmmIcon name="check" :size="14" /> 保存</button>
      </template>
    </el-dialog>

    <!-- 新增用户 -->
    <el-dialog v-model="newUser.visible" title="新增用户" width="420px">
      <div class="vstack" style="--gap: 14px">
        <div class="field">
          <div class="field-label">账号</div>
          <input class="input" v-model="newUser.username" autocomplete="off" />
        </div>
        <div class="field">
          <div class="field-label">密码</div>
          <input class="input" type="password" v-model="newUser.password" placeholder="至少 6 位" autocomplete="new-password" />
        </div>
        <div class="field">
          <div class="field-label">角色</div>
          <select class="select" v-model="newUser.role">
            <option value="Admin">Admin（全部权限）</option>
            <option value="Viewer">Viewer（只读）</option>
          </select>
        </div>
      </div>
      <template #footer>
        <button class="btn btn-ghost" @click="newUser.visible = false">取消</button>
        <button class="btn btn-primary" @click="onCreate"><PmmIcon name="check" :size="14" /> 创建</button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped lang="scss">
.user-avatar {
  width: 28px;
  height: 28px;
  border-radius: 999px;
  background: var(--accent-soft);
  color: var(--accent);
  display: grid;
  place-items: center;
  font-weight: 700;
  font-size: 12px;
}
.field { display: flex; flex-direction: column; gap: 6px; }
.field-label { font-size: 12.5px; font-weight: 600; color: var(--text); }
.small { font-size: 11.5px; }
.table-wrap { overflow: auto; }
</style>
