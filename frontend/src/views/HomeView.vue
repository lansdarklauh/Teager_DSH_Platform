<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApartmentOutlined, AppstoreOutlined, FolderOpenOutlined, ThunderboltOutlined, PlusOutlined, RightOutlined } from '@ant-design/icons-vue'
import { poolApi, wsApi, spaceApi, usageApi } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { fmtAgo, fmtCompact } from '@/utils/format'
import StatCard from '@/components/StatCard.vue'
import PoolTreeView from '@/components/PoolTreeView.vue'
import CreatePoolModal from '@/components/CreatePoolModal.vue'
import CreateWorkspaceModal from '@/components/CreateWorkspaceModal.vue'

const router = useRouter()
const auth = useAuthStore()
const pools = ref<any[]>([])
const workspaces = ref<any[]>([])
const spaces = ref<any[]>([])
const usage = ref<any>(null)
const loading = ref(true)
const showPool = ref(false)
const showWs = ref(false)

async function load() {
  loading.value = true
  try {
    ;[pools.value, workspaces.value, spaces.value, usage.value] = await Promise.all([poolApi.tree(), wsApi.list(), spaceApi.list(), usageApi.summary({ days: 1 })]) as any
  } finally { loading.value = false }
}
onMounted(load)
const recent = computed(() => [...workspaces.value].sort((a, b) => (b.lastActiveAt || b.createdAt || '').localeCompare(a.lastActiveAt || a.createdAt || '')).slice(0, 8))
const greeting = computed(() => { const h = new Date().getHours(); return h < 6 ? '夜深了' : h < 12 ? '早上好' : h < 18 ? '下午好' : '晚上好' })
const effective = computed(() => auth.user?.effectivePools || [])
</script>

<template>
  <div class="page stack">
    <div class="hello">
      <div>
        <h1>{{ greeting }}，{{ auth.displayName }}</h1>
        <p class="muted">下面是你可访问的 Pool、工作空间与共享资源。权限来自你所属的 Pool 及其上级 Pool 的向下继承。</p>
      </div>
      <div class="row-wrap">
        <a-button @click="showPool = true"><PlusOutlined /> 新建 Pool</a-button>
        <a-button type="primary" @click="showWs = true"><PlusOutlined /> 新建 Workspace</a-button>
      </div>
    </div>

    <div class="grid grid-4">
      <StatCard label="我的 Pool" :value="pools.length" mod="pool"><template #icon><ApartmentOutlined /></template></StatCard>
      <StatCard label="可访问 Workspace" :value="workspaces.length" mod="ws"><template #icon><AppstoreOutlined /></template></StatCard>
      <StatCard label="可用共享空间" :value="spaces.length" mod="space"><template #icon><FolderOpenOutlined /></template></StatCard>
      <StatCard label="今日 Agent 运行" :value="usage?.totals?.runs ?? 0" :hint="`${fmtCompact((usage?.totals?.inputTokens || 0) + (usage?.totals?.outputTokens || 0))} tokens`" mod="usage"><template #icon><ThunderboltOutlined /></template></StatCard>
    </div>

    <div class="grid" style="grid-template-columns: minmax(0, 5fr) minmax(0, 7fr)">
      <div class="card" data-mod="pool">
        <div class="card-title"><span class="chip sm"><ApartmentOutlined /></span>我的 Pool<span class="sub">点击进入 Pool 详情</span></div>
        <a-spin :spinning="loading"><PoolTreeView :nodes="pools" /></a-spin>
        <div v-if="effective.length" class="mt-24">
          <div class="muted small" style="margin-bottom: 8px">我的有效 Pool（直属 + 全部祖先）</div>
          <div class="row-wrap" style="gap: 8px">
            <span v-for="p in effective" :key="p.id" class="tag" :class="p.direct ? 'pool' : 'space'">{{ p.name }}<small>{{ p.direct ? '直属' : '继承' }}</small></span>
          </div>
        </div>
      </div>

      <div class="card" data-mod="ws">
        <div class="card-title"><span class="chip sm"><AppstoreOutlined /></span>最近的工作空间<span class="spacer" /><a @click="router.push('/workspaces')">全部 <RightOutlined /></a></div>
        <a-spin :spinning="loading">
          <div v-if="recent.length" class="grid grid-2" style="gap: 14px">
            <div v-for="w in recent" :key="w.id" class="ws card hover" @click="router.push(`/w/${w.id}`)">
              <div class="row"><span class="chip sm solid"><AppstoreOutlined /></span><b class="ellipsis">{{ w.name }}</b></div>
              <div class="muted small ellipsis" style="margin: 8px 0 12px">{{ w.poolName }}</div>
              <div class="row"><span class="tag ws">{{ w.myRole === 'Owner' ? '所有者' : w.myRole === 'Admin' ? '管理员' : w.myRole === 'Member' ? '成员' : w.myRole === 'Viewer' ? '只读' : '仅管理' }}</span><span class="spacer" /><span class="muted small">{{ fmtAgo(w.lastActiveAt) }}</span></div>
            </div>
          </div>
          <a-empty v-else description="还没有工作空间，点击右上角新建一个吧" />
        </a-spin>
      </div>
    </div>

    <CreatePoolModal v-model:open="showPool" @created="load" />
    <CreateWorkspaceModal v-model:open="showWs" @created="(w: any) => router.push(`/workspaces/${w.id}`)" />
  </div>
</template>

<style scoped>
.hello { display: flex; align-items: center; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
.hello h1 { margin: 0 0 6px; font-size: 26px; letter-spacing: -.4px; }
.hello p { margin: 0; }
.ws { padding: 16px 18px; background: var(--surface-2); }
</style>
