<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { UserOutlined, LockOutlined, ApartmentOutlined, AppstoreOutlined, FolderOpenOutlined, ThunderboltOutlined } from '@ant-design/icons-vue'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()
const form = ref({ username: '', password: '' })
const loading = ref(false)
const error = ref('')

async function submit() {
  if (!form.value.username || !form.value.password) { error.value = '请输入用户名和密码'; return }
  loading.value = true; error.value = ''
  try {
    await auth.login(form.value.username.trim(), form.value.password)
    const redirect = route.query.redirect as string
    router.replace(!auth.isAdmin ? auth.landingPath : redirect?.startsWith('/') && !redirect.startsWith('//') ? redirect : auth.landingPath)
  } catch (e: any) {
    error.value = e.response?.status === 429 ? '登录尝试过于频繁，请稍后再试' : (e.friendly || '登录失败')
  } finally { loading.value = false }
}
</script>

<template>
  <div class="login">
    <section class="hero">
      <div class="blob b1" /><div class="blob b2" /><div class="blob b3" />
      <div class="hero-inner">
        <div class="logo">D</div>
        <h1>DSH 多工作空间平台</h1>
        <p>以层级 Pool 组织团队与资源，以 Workspace 承载 Agent 工作，通过共享空间跨团队协作——一个统一入口，统一身份与 API。</p>
        <ul>
          <li><span class="chip" data-mod="pool"><ApartmentOutlined /></span><div><b>层级 Pool</b><small>父级资源向下继承，权限来源可解释</small></div></li>
          <li><span class="chip" data-mod="ws"><AppstoreOutlined /></span><div><b>独立 Workspace</b><small>文件、会话、任务上下文彼此隔离</small></div></li>
          <li><span class="chip" data-mod="space"><FolderOpenOutlined /></span><div><b>共享空间</b><small>RO / RW，按 Pool 授权并按需挂载</small></div></li>
          <li><span class="chip" data-mod="runtime"><ThunderboltOutlined /></span><div><b>可插拔 Runtime</b><small>DSH 作为内部运行时，对外只暴露平台 API</small></div></li>
        </ul>
      </div>
    </section>
    <section class="form-side">
      <div class="panel">
        <h2>欢迎回来</h2>
        <p class="muted">请使用平台账号登录</p>
        <a-form :model="form" layout="vertical" style="margin-top: 28px" @finish="submit">
          <a-form-item label="用户名"><a-input v-model:value="form.username" size="large" placeholder="登录名" autocomplete="username"><template #prefix><UserOutlined class="muted" /></template></a-input></a-form-item>
          <a-form-item label="密码"><a-input-password v-model:value="form.password" size="large" placeholder="密码" autocomplete="current-password" @press-enter="submit"><template #prefix><LockOutlined class="muted" /></template></a-input-password></a-form-item>
          <a-alert v-if="error" type="error" :message="error" show-icon style="margin-bottom: 18px" />
          <a-button type="primary" size="large" block :loading="loading" html-type="submit" style="height: 46px; font-weight: 600">登 录</a-button>
        </a-form>
        <div class="foot muted small">登录后统一使用平台会话（HttpOnly Cookie）；SDK / API 请使用个人 Token。</div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.login { display: flex; min-height: 100vh; background: var(--surface); }
.hero { flex: 1.15; position: relative; overflow: hidden; background: #4338ca; color: #fff; display: flex; align-items: center; padding: 64px; }
.blob { position: absolute; border-radius: 50%; opacity: .95; }
.b1 { width: 380px; height: 380px; background: #5b52f0; right: -120px; top: -100px; }
.b2 { width: 220px; height: 220px; background: #f59e0b; right: 60px; bottom: -70px; opacity: .9; }
.b3 { width: 140px; height: 140px; background: #14b8a6; left: -40px; bottom: 90px; opacity: .85; }
.hero-inner { position: relative; max-width: 540px; z-index: 1; }
.logo { width: 56px; height: 56px; border-radius: 16px; background: #fff; color: #4338ca; font-weight: 800; font-size: 30px; display: flex; align-items: center; justify-content: center; margin-bottom: 28px; }
h1 { color: #fff; font-size: 34px; margin: 0 0 14px; letter-spacing: -.5px; }
.hero p { color: rgba(255,255,255,.82); font-size: 15px; line-height: 1.85; margin: 0 0 36px; }
ul { list-style: none; padding: 0; margin: 0; display: grid; gap: 18px; }
li { display: flex; align-items: center; gap: 16px; background: rgba(255,255,255,.1); padding: 14px 18px; border-radius: 16px; }
li b { display: block; font-size: 15px; } li small { color: rgba(255,255,255,.75); font-size: 13px; }
li .chip { background: #fff; }
.form-side { flex: 1; display: flex; align-items: center; justify-content: center; padding: 48px; }
.panel { width: 100%; max-width: 400px; }
h2 { font-size: 28px; margin: 0 0 6px; letter-spacing: -.4px; }
.foot { margin-top: 28px; line-height: 1.7; }
@media (max-width: 900px) { .hero { display: none; } }
</style>
