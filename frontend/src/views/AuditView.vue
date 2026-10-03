<script setup lang="ts">
import { computed } from 'vue'
import { AuditOutlined } from '@ant-design/icons-vue'
import { useAuthStore } from '@/stores/auth'
import PageHeader from '@/components/PageHeader.vue'
import AuditPanel from '@/components/AuditPanel.vue'

const auth = useAuthStore()
const sub = computed(() => (auth.isAdmin ? '记录谁、何时、从哪里、对什么资源做了什么（全平台）' : '你可见的审计范围：你自己的操作，以及你管理的 Pool 内发生的操作'))
</script>

<template>
  <div class="page" data-mod="audit">
    <PageHeader title="审计日志" :sub="sub"><template #icon><AuditOutlined /></template></PageHeader>
    <AuditPanel />
  </div>
</template>
