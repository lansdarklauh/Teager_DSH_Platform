<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { userApi } from '@/api'

/** 用户远程检索选择器（按登录名 / 显示名）。 */
const model = defineModel<string | undefined>({ required: false })
defineProps<{ placeholder?: string; excludeService?: boolean }>()
const options = ref<any[]>([])
const loading = ref(false)
let timer: any
async function search(q = '') {
  loading.value = true
  try { options.value = (await userApi.lookup(q)) as any[] } finally { loading.value = false }
}
function onSearch(v: string) { clearTimeout(timer); timer = setTimeout(() => search(v), 250) }
onMounted(() => search())
</script>

<template>
  <a-select v-model:value="model" show-search :filter-option="false" :loading="loading" :placeholder="placeholder || '搜索用户…'" style="width: 100%" allow-clear @search="onSearch">
    <a-select-option v-for="u in options.filter((x) => !excludeService || !x.isServiceAccount)" :key="u.id" :value="u.id">
      {{ u.displayName }}<span class="muted">（{{ u.username }}）</span><a-tag v-if="u.isServiceAccount" color="cyan" style="margin-left: 6px">服务账号</a-tag>
    </a-select-option>
  </a-select>
</template>
