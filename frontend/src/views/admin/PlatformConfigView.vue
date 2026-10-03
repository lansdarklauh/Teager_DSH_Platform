<script setup lang="ts">
import { ref } from 'vue'
import { SettingOutlined } from '@ant-design/icons-vue'
import PageHeader from '@/components/PageHeader.vue'
import ConfigPanel from '@/components/ConfigPanel.vue'
import SecretPanel from '@/components/SecretPanel.vue'

const tab = ref('Model')
</script>

<template>
  <div class="page" data-mod="config">
    <PageHeader title="平台配置" sub="平台 / 组织级默认：模型、Skill、MCP 与密钥。逐级向下继承：平台默认 → 父 Pool → 子 Pool → Workspace">
      <template #icon><SettingOutlined /></template>
    </PageHeader>
    <div class="note" style="margin-bottom: 24px"><span>子级只能<b>收紧</b>（停用）上级提供的项，不能重新启用被上级停用的项；子级新增全新的模型 / MCP 需要平台管理员授权（在 Pool 的“策略”中开启）。API Key 等敏感信息请放在“密钥”，配置里只引用环境变量名。</span></div>
    <a-tabs v-model:active-key="tab" :destroy-inactive-tab-pane="true">
      <a-tab-pane key="Model" tab="模型"><ConfigPanel scope="Platform" kind="Model" :can-edit="true" /></a-tab-pane>
      <a-tab-pane key="Skill" tab="Skills"><ConfigPanel scope="Platform" kind="Skill" :can-edit="true" /></a-tab-pane>
      <a-tab-pane key="Mcp" tab="MCP"><ConfigPanel scope="Platform" kind="Mcp" :can-edit="true" /></a-tab-pane>
      <a-tab-pane key="Secret" tab="组织密钥"><SecretPanel scope="Platform" /></a-tab-pane>
    </a-tabs>
  </div>
</template>
