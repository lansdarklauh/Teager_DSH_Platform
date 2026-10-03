<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { poolApi } from '@/api'
import { buildPoolTree } from '@/utils/tree'

/** Pool 树选择器：可限定只显示可管理的 Pool、排除某些 ID。 */
const model = defineModel<string | undefined>({ required: false })
const props = defineProps<{ placeholder?: string; manageOnly?: boolean; exclude?: string[]; allowClear?: boolean }>()
const nodes = ref<any[]>([])
onMounted(async () => { nodes.value = (await poolApi.tree()) as any[] })
const data = computed(() => {
  const ex = new Set(props.exclude || [])
  const list = nodes.value.filter((n) => !ex.has(n.id))
  const tree = buildPoolTree(list)
  const mark = (arr: any[]): any[] => arr.map((n) => ({ ...n, disabled: props.manageOnly && !n.raw.canManage, children: mark(n.children) }))
  return mark(tree)
})
</script>

<template>
  <a-tree-select v-model:value="model" :tree-data="data" :placeholder="placeholder || '选择 Pool'" tree-default-expand-all show-search tree-node-filter-prop="title" style="width: 100%" :allow-clear="allowClear" />
</template>
