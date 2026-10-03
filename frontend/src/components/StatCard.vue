<script setup lang="ts">
/** 指标卡：左侧模块色图标块，右侧数值与说明。 */
defineProps<{ label: string; value: string | number; mod?: string; hint?: string }>()
</script>

<template>
  <div class="card stat" :data-mod="mod || 'pool'">
    <span class="chip lg"><slot name="icon" /></span>
    <div>
      <div class="num">{{ value }}</div>
      <div class="label">{{ label }}<span v-if="hint" class="muted small"> · {{ hint }}</span></div>
    </div>
  </div>
</template>
