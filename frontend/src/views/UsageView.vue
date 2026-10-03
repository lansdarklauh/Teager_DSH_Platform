<script setup lang="ts">
import { computed, ref } from 'vue'
import { BarChartOutlined } from '@ant-design/icons-vue'
import { useAuthStore } from '@/stores/auth'
import PageHeader from '@/components/PageHeader.vue'
import UsagePanel from '@/components/UsagePanel.vue'
import PoolTreeSelect from '@/components/PoolTreeSelect.vue'

const auth = useAuthStore()
const poolId = ref<string | undefined>(undefined)
const sub = computed(() => (poolId.value ? '所选 Pool（含子 Pool）的用量' : auth.isAdmin ? '全平台用量；可选择 Pool 查看明细' : '我的个人用量；选择 Pool 可查看该 Pool 内的用量'))
</script>

<template>
  <div class="page" data-mod="usage">
    <PageHeader title="用量统计" :sub="sub">
      <template #icon><BarChartOutlined /></template>
      <template #actions><div style="width: 280px"><PoolTreeSelect v-model="poolId" allow-clear placeholder="选择 Pool 查看明细" /></div></template>
    </PageHeader>
    <UsagePanel :pool-id="poolId" />
  </div>
</template>
