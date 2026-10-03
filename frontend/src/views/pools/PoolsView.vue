<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApartmentOutlined, PlusOutlined, ArrowUpOutlined, ArrowDownOutlined, StopOutlined } from '@ant-design/icons-vue'
import { poolApi } from '@/api'
import PageHeader from '@/components/PageHeader.vue'
import PoolTreeView from '@/components/PoolTreeView.vue'
import CreatePoolModal from '@/components/CreatePoolModal.vue'

const router = useRouter()
const nodes = ref<any[]>([])
const loading = ref(true)
const show = ref(false)
async function load() { loading.value = true; try { nodes.value = (await poolApi.tree()) as any[] } finally { loading.value = false } }
onMounted(load)
</script>

<template>
  <div class="page" data-mod="pool">
    <PageHeader title="Pool 管理" sub="Pool 是逻辑工作空间组（部门 / 项目组 / 业务线…），严格单父树，最多 5 层">
      <template #icon><ApartmentOutlined /></template>
      <template #actions><a-button type="primary" @click="show = true"><PlusOutlined /> 新建 Pool</a-button></template>
    </PageHeader>
    <div class="grid" style="grid-template-columns: minmax(0, 8fr) minmax(0, 4fr); align-items: start">
      <div class="card">
        <a-spin :spinning="loading"><PoolTreeView :nodes="nodes" /></a-spin>
      </div>
      <div class="card">
        <div class="card-title">层级访问规则</div>
        <div class="info-list">
          <div class="info-item"><span class="chip sm" data-mod="ws"><ArrowDownOutlined /></span><div><b>父 → 子：可继承</b><div class="muted small">父 Pool 向下共享的资源，子 Pool 成员都能用。</div></div></div>
          <div class="info-item"><span class="chip sm" data-mod="audit"><ArrowUpOutlined /></span><div><b>子 → 父：默认不继承</b><div class="muted small">子 Pool 的私有资源不会自动向上暴露。</div></div></div>
          <div class="info-item"><span class="chip sm" data-mod="admin"><StopOutlined /></span><div><b>兄弟 Pool：默认不共享</b><div class="muted small">同属一个父级也不会互相看到对方资源。</div></div></div>
        </div>
        <div class="note mt-16"><span>用户的<b>有效 Pool</b> = 直属 Pool + 全部祖先 Pool。需要跨层级共享时，请显式授权（默认最小权限）。</span></div>
      </div>
    </div>
    <CreatePoolModal v-model:open="show" @created="(p: any) => router.push(`/p/${p.id}`)" />
  </div>
</template>
