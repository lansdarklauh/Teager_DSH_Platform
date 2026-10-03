<script setup lang="ts">
import { ArrowLeftOutlined } from '@ant-design/icons-vue'
import { useRouter } from 'vue-router'

/** 页面头部：模块色图标块 + 标题 + 副标题 + 标签 + 操作区。 */
defineProps<{ title: string; sub?: string; back?: string }>()
const router = useRouter()
</script>

<template>
  <div class="ph">
    <a-button v-if="back" type="text" shape="circle" class="back" @click="router.push(back)"><ArrowLeftOutlined /></a-button>
    <span v-if="$slots.icon" class="chip lg solid"><slot name="icon" /></span>
    <div class="txt">
      <div class="row-wrap">
        <h1>{{ title }}</h1>
        <slot name="tags" />
      </div>
      <div v-if="sub || $slots.sub" class="sub"><slot name="sub">{{ sub }}</slot></div>
    </div>
    <div class="spacer" />
    <div class="row-wrap"><slot name="actions" /></div>
  </div>
</template>

<style scoped>
.ph { display: flex; align-items: center; gap: 16px; margin-bottom: 28px; flex-wrap: wrap; }
.txt { min-width: 0; }
h1 { margin: 0; font-size: 22px; font-weight: 700; letter-spacing: -.3px; }
.sub { color: var(--text-3); margin-top: 4px; font-size: 13px; }
.back { margin-left: -8px; color: var(--text-2); }
</style>
