<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { FolderOpenOutlined, PlusOutlined } from '@ant-design/icons-vue'
import { spaceApi } from '@/api'
import { fmtBytes } from '@/utils/format'
import PageHeader from '@/components/PageHeader.vue'
import Tags from '@/components/Tags.vue'
import CreateSpaceModal from '@/components/CreateSpaceModal.vue'

const router = useRouter()
const list = ref<any[]>([])
const loading = ref(true)
const archived = ref(false)
const keyword = ref('')
const show = ref(false)
async function load() { loading.value = true; try { list.value = ((await spaceApi.list({ includeArchived: archived.value ? true : undefined })) as any[]) } finally { loading.value = false } }
onMounted(load)
watch(archived, load)
const filtered = () => list.value.filter((s) => !keyword.value || (s.name + (s.description || '') + s.ownerPoolName).toLowerCase().includes(keyword.value.toLowerCase()))
</script>

<template>
  <div class="page" data-mod="space">
    <PageHeader title="共享空间" sub="独立的一级资源：授权给 Pool / 用户（RO / RW / MANAGE），可向子 Pool 继承，并由 Workspace 按需挂载">
      <template #icon><FolderOpenOutlined /></template>
      <template #actions><a-button type="primary" @click="show = true"><PlusOutlined /> 新建共享空间</a-button></template>
    </PageHeader>
    <div class="card" style="margin-bottom: 24px"><div class="row-wrap"><a-input-search v-model:value="keyword" placeholder="搜索名称 / 描述 / 所属" style="width: 300px" allow-clear /><a-checkbox v-model:checked="archived">显示已归档</a-checkbox></div></div>
    <a-spin :spinning="loading">
      <div v-if="filtered().length" class="grid grid-auto">
        <div v-for="s in filtered()" :key="s.id" class="card hover sp" @click="router.push(`/shared-spaces/${s.id}`)">
          <div class="row"><span class="chip solid"><FolderOpenOutlined /></span><div style="min-width: 0"><b class="ellipsis" style="font-size: 15px">{{ s.name }}</b><div class="muted small ellipsis">所属：{{ s.ownerPoolName }}</div></div><span class="spacer" /><span v-if="s.status === 'Archived'" class="tag warn">已归档</span></div>
          <div class="muted small desc">{{ s.description || '暂无描述' }}</div>
          <div class="row-wrap" style="gap: 8px"><Tags kind="role" :value="s.permission" /><Tags kind="source" :value="s.source" :path="s.path" /></div>
          <div class="row"><span class="mono muted small">shared/{{ s.mountName }}</span><span class="spacer" /><span class="muted small">{{ fmtBytes(s.storageBytes) }}{{ s.quotaMb ? ` / ${s.quotaMb} MB` : '' }}</span></div>
        </div>
      </div>
      <a-empty v-else description="没有你可访问的共享空间" style="padding: 60px 0" />
    </a-spin>
    <CreateSpaceModal v-model:open="show" @created="(s: any) => router.push(`/shared-spaces/${s.id}`)" />
  </div>
</template>

<style scoped>
.sp { display: flex; flex-direction: column; gap: 14px; }
.desc { min-height: 38px; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
</style>
