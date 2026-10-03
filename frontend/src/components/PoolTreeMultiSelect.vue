<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { poolApi } from '@/api'
import { buildPoolTree } from '@/utils/tree'

/** Pool 树多选器（如 Token 的 Pool 白名单）。 */
const model = defineModel<string[]>({ default: () => [] })
const nodes = ref<any[]>([])
onMounted(async () => { nodes.value = (await poolApi.tree()) as any[] })
const data = computed(() => buildPoolTree(nodes.value))
</script>

<template>
  <a-tree-select v-model:value="model" :tree-data="data" multiple tree-default-expand-all show-search tree-node-filter-prop="title" placeholder="选择 Pool" style="width: 100%" />
</template>
