<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { PlusOutlined, FolderOpenOutlined } from '@ant-design/icons-vue'
import { poolApi } from '@/api'
import { fmtBytes } from '@/utils/format'
import Tags from './Tags.vue'
import CreateSpaceModal from './CreateSpaceModal.vue'

/** Pool 的共享空间：直接授权 / 继承授权（来自祖先 Pool，显示继承路径）/ 本 Pool 拥有。 */
const props = defineProps<{ poolId: string; canManage: boolean }>()
const emit = defineEmits<{ loaded: [spaces: any[]] }>()
const router = useRouter()
const data = ref<any>({ direct: [], inherited: [], owned: [] })
const loading = ref(false)
const show = ref(false)
async function load() {
  loading.value = true
  try { data.value = await poolApi.sharedSpaces(props.poolId); emit('loaded', [...data.value.direct, ...data.value.inherited].filter((x: any) => !x.denied)) } finally { loading.value = false }
}
watch(() => props.poolId, load)
onMounted(load)
</script>

<template>
  <div class="stack" data-mod="space">
    <div class="row"><div class="muted">下列共享空间对本 Pool 的 Workspace 可用，需在 Workspace 内<b>挂载</b>后才会出现在 Agent 的 <span class="mono">shared/</span> 目录。</div><span class="spacer" /><a-button v-if="canManage" type="primary" @click="show = true"><PlusOutlined /> 新建共享空间</a-button></div>
    <div v-for="blk in [{ t: '直接授权', d: '直接授权给本 Pool', k: 'direct' }, { t: '继承授权', d: '来自祖先 Pool，且开启了“向下继承”', k: 'inherited' }, { t: '本 Pool 拥有', d: '由本 Pool 创建并管理', k: 'owned' }]" :key="blk.k" class="card flush">
      <div class="card-head"><h3>{{ blk.t }}<span class="muted small" style="font-weight: 400">　{{ blk.d }}</span></h3><span class="tag space">{{ data[blk.k].length }}</span></div>
      <a-table :data-source="data[blk.k]" :loading="loading" row-key="id" :pagination="false" size="middle" :locale="{ emptyText: '无' }">
        <a-table-column title="共享空间" key="n"><template #default="{ record }"><div class="row" style="cursor: pointer" @click="router.push(`/shared-spaces/${record.id}`)"><span class="chip sm"><FolderOpenOutlined /></span><div><b>{{ record.name }}</b><div class="muted small">{{ record.description }}</div></div></div></template></a-table-column>
        <a-table-column title="挂载名" key="m" :width="150"><template #default="{ record }"><span class="mono">{{ record.mountName }}</span></template></a-table-column>
        <a-table-column v-if="blk.k !== 'owned'" title="权限" key="p" :width="130"><template #default="{ record }"><span v-if="record.denied" class="tag danger dot">显式拒绝</span><Tags v-else kind="role" :value="record.permission" /></template></a-table-column>
        <a-table-column v-if="blk.k === 'inherited'" title="继承路径" key="path"><template #default="{ record }"><span class="tag space">{{ record.path.join(' → ') }}</span></template></a-table-column>
        <a-table-column title="占用" key="s" :width="110"><template #default="{ record }"><span class="muted">{{ fmtBytes(record.storageBytes) }}</span></template></a-table-column>
      </a-table>
    </div>
    <CreateSpaceModal v-model:open="show" :pool-id="poolId" @created="(s: any) => router.push(`/shared-spaces/${s.id}`)" />
  </div>
</template>
