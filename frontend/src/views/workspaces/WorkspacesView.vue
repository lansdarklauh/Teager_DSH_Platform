<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { AppstoreOutlined, PlusOutlined } from '@ant-design/icons-vue'
import { wsApi } from '@/api'
import PageHeader from '@/components/PageHeader.vue'
import WorkspaceCard from '@/components/WorkspaceCard.vue'
import PoolTreeSelect from '@/components/PoolTreeSelect.vue'
import CreateWorkspaceModal from '@/components/CreateWorkspaceModal.vue'

const router = useRouter()
const list = ref<any[]>([])
const loading = ref(true)
const poolId = ref<string | undefined>(undefined)
const keyword = ref('')
const archived = ref(false)
const show = ref(false)
async function load() { loading.value = true; try { list.value = (await wsApi.list({ poolId: poolId.value, includeDescendants: poolId.value ? true : undefined, includeArchived: archived.value ? true : undefined })) as any[] } finally { loading.value = false } }
onMounted(load)
watch([poolId, archived], load)
const filtered = computed(() => list.value.filter((w) => !keyword.value || (w.name + w.poolName + (w.description || '')).toLowerCase().includes(keyword.value.toLowerCase())))
</script>

<template>
  <div class="page" data-mod="ws">
    <PageHeader title="工作空间" sub="Workspace 是 Agent 真正工作的目录与会话边界；每个 Workspace 只属于一个 Pool">
      <template #icon><AppstoreOutlined /></template>
      <template #actions><a-button type="primary" @click="show = true"><PlusOutlined /> 新建 Workspace</a-button></template>
    </PageHeader>
    <div class="card" style="margin-bottom: 24px">
      <div class="row-wrap">
        <div style="width: 260px"><PoolTreeSelect v-model="poolId" allow-clear placeholder="按 Pool 筛选（含子 Pool）" /></div>
        <a-input-search v-model:value="keyword" placeholder="搜索名称 / 描述" style="width: 260px" allow-clear />
        <a-checkbox v-model:checked="archived">显示已归档</a-checkbox>
        <span class="spacer" /><span class="muted">共 {{ filtered.length }} 个</span>
      </div>
    </div>
    <a-spin :spinning="loading">
      <div v-if="filtered.length" class="grid grid-auto"><WorkspaceCard v-for="w in filtered" :key="w.id" :ws="w" /></div>
      <a-empty v-else description="没有符合条件的 Workspace" style="padding: 60px 0" />
    </a-spin>
    <CreateWorkspaceModal v-model:open="show" :pool-id="poolId" @created="(w: any) => router.push(`/workspaces/${w.id}`)" />
  </div>
</template>
