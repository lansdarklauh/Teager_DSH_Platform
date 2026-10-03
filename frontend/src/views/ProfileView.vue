<script setup lang="ts">
import { ref } from 'vue'
import { message } from 'ant-design-vue'
import { UserOutlined } from '@ant-design/icons-vue'
import { authApi } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { fmtTime } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import Tags from '@/components/Tags.vue'

const auth = useAuthStore()
const form = ref({ oldPassword: '', newPassword: '', confirm: '' })
const saving = ref(false)
async function change() {
  if (form.value.newPassword !== form.value.confirm) { message.warning('两次输入的新密码不一致'); return }
  saving.value = true
  try { await authApi.changePassword(form.value.oldPassword, form.value.newPassword); message.success('密码已修改，其他设备的登录已失效'); form.value = { oldPassword: '', newPassword: '', confirm: '' } } finally { saving.value = false }
}
</script>

<template>
  <div class="page" data-mod="admin">
    <PageHeader title="个人设置" sub="账号信息与安全"><template #icon><UserOutlined /></template></PageHeader>
    <div class="grid grid-2" style="align-items: start">
      <div class="card">
        <div class="card-title">账号信息</div>
        <dl class="kv">
          <dt>显示名</dt><dd>{{ auth.user?.displayName }}</dd>
          <dt>登录名</dt><dd class="mono">{{ auth.user?.username }}</dd>
          <dt>平台角色</dt><dd>{{ auth.isAdmin ? '平台管理员' : '普通用户' }}</dd>
          <dt>最近登录</dt><dd>{{ fmtTime(auth.user?.lastLoginAt) }}</dd>
          <dt>直属 Pool</dt><dd><div class="row-wrap" style="gap: 8px"><span v-for="p in auth.user?.directPools" :key="p.id" class="tag pool">{{ p.name }} <Tags kind="role" :value="p.role" /></span><span v-if="!auth.user?.directPools?.length" class="muted">无</span></div></dd>
        </dl>
      </div>
      <div class="card">
        <div class="card-title">修改密码</div>
        <a-form layout="vertical">
          <a-form-item label="当前密码"><a-input-password v-model:value="form.oldPassword" autocomplete="current-password" /></a-form-item>
          <a-form-item label="新密码（至少 8 位，需含字母和数字）"><a-input-password v-model:value="form.newPassword" autocomplete="new-password" /></a-form-item>
          <a-form-item label="确认新密码"><a-input-password v-model:value="form.confirm" autocomplete="new-password" /></a-form-item>
          <a-button type="primary" :loading="saving" @click="change">修改密码</a-button>
        </a-form>
      </div>
    </div>
  </div>
</template>
