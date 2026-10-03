<script setup lang="ts">
import { useRouter } from 'vue-router'
import { AppstoreOutlined, ArrowRightOutlined, InboxOutlined } from '@ant-design/icons-vue'
import { fmtAgo, fmtBytes } from '@/utils/format'
import Tags from './Tags.vue'

/** Workspace 卡片：名称、所属 Pool、我的角色（含继承来源提示）、存储与最近活跃。 */
defineProps<{ ws: any }>()
const router = useRouter()
</script>

<template>
  <div class="card hover wc" data-mod="ws" @click="router.push(`/workspaces/${ws.id}`)">
    <div class="row">
      <span class="chip solid"><AppstoreOutlined /></span>
      <div class="ellipsis" style="min-width: 0"><b class="ellipsis" style="font-size: 15px">{{ ws.name }}</b><div class="muted small ellipsis">{{ ws.poolName }}</div></div>
      <span class="spacer" />
      <span v-if="ws.status === 'Archived'" class="tag warn"><InboxOutlined /> 已归档</span>
    </div>
    <div class="desc muted small">{{ ws.description || '暂无描述' }}</div>
    <div class="row-wrap" style="gap: 8px">
      <Tags v-if="ws.myRole" kind="role" :value="ws.myRole" :path="ws.path" />
      <span v-else class="tag">仅管理</span>
      <span v-if="ws.source === 'pool-inherited'" class="tag space">继承</span>
    </div>
    <div class="row foot">
      <span class="muted small">{{ fmtBytes(ws.storageBytes) }} · {{ fmtAgo(ws.lastActiveAt) }}</span>
      <span class="spacer" />
      <a-button v-if="ws.myRole && ws.status !== 'Archived'" type="primary" size="small" @click.stop="router.push(`/w/${ws.id}`)">进入工作台 <ArrowRightOutlined /></a-button>
    </div>
  </div>
</template>

<style scoped>
.wc { display: flex; flex-direction: column; gap: 14px; }
.desc { min-height: 38px; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.foot { margin-top: 2px; }
</style>
