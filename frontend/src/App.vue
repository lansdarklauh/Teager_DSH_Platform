<script setup lang="ts">
import zhCN from 'ant-design-vue/es/locale/zh_CN'
import dayjs from 'dayjs'
import 'dayjs/locale/zh-cn'
dayjs.locale('zh-cn')

/** 全局主题：圆润、扁平；主色随页面模块变化由 CSS 变量提供，这里设置 antd 的基础令牌。 */
const theme = {
  token: { colorPrimary: '#4f46e5', borderRadius: 9, fontSize: 14, colorText: '#1f2433', colorBorder: '#e2e5ee', controlHeight: 36, fontFamily: `-apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC', 'Microsoft YaHei', sans-serif` },
  components: { Button: { controlHeight: 36 }, Table: { headerBg: '#fafbfd' } }
}
</script>

<template>
  <a-config-provider :locale="zhCN" :theme="theme">
    <router-view />
  </a-config-provider>
</template>
