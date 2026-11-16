<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { message } from 'ant-design-vue'
import { poolApi } from '@/api'
import PageHeader from '@/components/PageHeader.vue'
const router = useRouter()
const pools = ref<any[]>([])
const selected = ref<any>(null)
async function load() { pools.value = await poolApi.tree() as any[] }
async function select(id: string) { selected.value = await poolApi.get(id) }
async function save() { await poolApi.update(selected.value.id, { name: selected.value.name, description: selected.value.description }); message.success('已保存'); await load() }
onMounted(load)
</script>
<template>
  <div class="page">
    <PageHeader title="可访问 Pool" sub="管理员负责空间、配置和成员管理；编辑授权仅允许修改名称与描述" />
    <div class="grid grid-2">
      <div class="card"><div v-for="p in pools" :key="p.id" class="row" style="padding: 12px 0"><b>{{ p.name }}</b><span class="spacer" /><a-button @click="select(p.id)">查看</a-button></div><a-empty v-if="!pools.length" description="暂无授权 Pool" /></div>
      <div v-if="selected" class="card"><a-form layout="vertical"><a-form-item label="名称"><a-input v-model:value="selected.name" :disabled="!selected.my.canEditDetails" /></a-form-item><a-form-item label="描述"><a-textarea v-model:value="selected.description" :disabled="!selected.my.canEditDetails" /></a-form-item><a-button v-if="selected.my.canEditDetails" type="primary" @click="save">保存基本信息</a-button><a-button @click="router.push('/shared-spaces')">查看授权共享资源</a-button></a-form></div>
    </div>
  </div>
</template>
