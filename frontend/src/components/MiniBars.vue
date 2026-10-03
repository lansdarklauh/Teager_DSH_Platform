<script setup lang="ts">
import { computed } from 'vue'

/** 轻量 SVG 柱状图（无第三方依赖）：支持双系列堆叠（如输入 / 输出 token）。 */
const props = defineProps<{ data: { label: string; a: number; b?: number }[]; height?: number; colorA?: string; colorB?: string; unitA?: string; unitB?: string }>()
const H = computed(() => props.height ?? 180)
const max = computed(() => Math.max(1, ...props.data.map((d) => d.a + (d.b || 0))))
const bw = computed(() => Math.max(6, Math.min(34, 720 / Math.max(props.data.length, 1) - 8)))
const step = computed(() => 720 / Math.max(props.data.length, 1))
const h = (v: number) => Math.round(((H.value - 30) * v) / max.value)
</script>

<template>
  <svg :viewBox="`0 0 720 ${H}`" preserveAspectRatio="none" class="bars" :style="{ height: H + 'px' }">
    <line v-for="i in 4" :key="i" x1="0" x2="720" :y1="(H - 24) - ((H - 30) * i) / 4" :y2="(H - 24) - ((H - 30) * i) / 4" stroke="#eef0f5" />
    <g v-for="(d, i) in data" :key="i">
      <title>{{ d.label }}：{{ unitA || '' }} {{ d.a }}{{ d.b !== undefined ? ` / ${unitB || ''} ${d.b}` : '' }}</title>
      <rect :x="i * step + (step - bw) / 2" :y="H - 24 - h(d.a)" :width="bw" :height="h(d.a)" rx="4" :fill="colorA || '#db2777'" />
      <rect v-if="d.b" :x="i * step + (step - bw) / 2" :y="H - 24 - h(d.a) - h(d.b)" :width="bw" :height="h(d.b)" rx="4" :fill="colorB || '#f9a8d4'" />
      <text v-if="data.length <= 16 || i % Math.ceil(data.length / 12) === 0" :x="i * step + step / 2" :y="H - 6" text-anchor="middle" font-size="11" fill="#8a91a6">{{ d.label }}</text>
    </g>
  </svg>
</template>

<style scoped>
.bars { width: 100%; display: block; }
</style>
