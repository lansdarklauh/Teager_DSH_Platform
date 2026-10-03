<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ApartmentOutlined, UserOutlined, AppstoreOutlined } from '@ant-design/icons-vue'
import { buildPoolTree, allKeys } from '@/utils/tree'
import Tags from './Tags.vue'

/** Pool 树：显示我的角色、访问来源、Workspace / 成员数；点击进入 Pool 详情。 */
const props = defineProps<{ nodes: any[]; selectable?: boolean; compact?: boolean }>()
const emit = defineEmits<{ select: [node: any] }>()
const router = useRouter()
const tree = computed(() => buildPoolTree(props.nodes))
const expanded = ref<string[]>([])
watch(() => props.nodes, () => { expanded.value = allKeys(tree.value) }, { immediate: true })
function click(_: any, { node }: any) { if (props.selectable) emit('select', node.raw); else router.push(`/p/${node.key}`) }
</script>

<template>
  <a-tree v-if="tree.length" :tree-data="tree" v-model:expanded-keys="expanded" block-node :selectable="true" @select="click">
    <template #title="{ raw }">
      <div class="node">
        <span class="chip sm" data-mod="pool"><ApartmentOutlined /></span>
        <b class="name">{{ raw.name }}</b>
        <Tags v-if="raw.directRole" kind="role" :value="raw.directRole" />
        <span v-else class="tag">{{ raw.access === 'descendant-member' ? '经由子 Pool 可见' : raw.access === 'ancestor-admin' ? '上级委托管理' : raw.access === 'platform-admin' ? '平台管理员' : '可见' }}</span>
        <span class="spacer" />
        <template v-if="!compact">
          <span class="muted small"><AppstoreOutlined /> {{ raw.workspaceCount }}</span>
          <span class="muted small"><UserOutlined /> {{ raw.memberCount }}</span>
        </template>
      </div>
    </template>
  </a-tree>
  <a-empty v-else description="暂无 Pool" />
</template>

<style scoped>
.node { display: flex; align-items: center; gap: 10px; padding: 6px 0; min-width: 280px; }
.name { font-weight: 600; }
:deep(.ant-tree-node-content-wrapper) { flex: 1; }
:deep(.ant-tree .ant-tree-treenode) { padding: 3px 0; }
</style>
