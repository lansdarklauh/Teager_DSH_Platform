<script setup lang="ts">
import { computed } from 'vue'
import { roleText, roleColor, runtimeText, runtimeColor, sourceText, sourceColor } from '@/utils/format'

/** 统一的标签：role（角色 / 权限）、runtime（Runtime 状态）、source（权限来源，可带继承路径提示）。 */
const props = defineProps<{ kind: 'role' | 'runtime' | 'source'; value?: string | null; path?: string[] | null; via?: string | null }>()
const text = computed(() => {
  const v = props.value || ''
  if (props.kind === 'role') return roleText[v] || v
  if (props.kind === 'runtime') return runtimeText[v] || v
  return sourceText[v] || v
})
const cls = computed(() => {
  const v = props.value || ''
  if (props.kind === 'role') return roleColor[v] || ''
  if (props.kind === 'runtime') return runtimeColor[v] || ''
  return sourceColor(v)
})
const tip = computed(() => (props.path?.length ? '继承路径：' + props.path.join(' → ') : props.via ? '来源：' + props.via : ''))
</script>

<template>
  <a-tooltip :title="tip" :open="tip ? undefined : false">
    <span v-if="value" class="tag dot" :class="[cls]">{{ text }}</span>
  </a-tooltip>
</template>
