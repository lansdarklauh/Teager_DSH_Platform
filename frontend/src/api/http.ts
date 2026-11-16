import axios, { type AxiosRequestConfig } from 'axios'
import { message } from 'ant-design-vue'
import router from '@/router'
import { useAuthStore } from '@/stores/auth'

/** 通用 axios 实例：携带 Cookie 会话；写请求带 CSRF 头（与后端 SameSite Cookie 配合）。 */
export const http = axios.create({
  baseURL: '/api/v1',
  withCredentials: true,
  timeout: 120000,
  headers: { 'X-Requested-With': 'dsh-web' }
})

/** 扩展配置：silent 为 true 时不弹全局错误提示（由调用方自行处理）。 */
declare module 'axios' {
  interface AxiosRequestConfig { silent?: boolean }
}

http.interceptors.response.use(
  (r) => r,
  (err) => {
    const status = err.response?.status
    const data = err.response?.data
    const msg = (data && typeof data === 'object' && data.message) || err.message || '请求失败'
    if (status === 401) useAuthStore().clearIdentity()
    if (status === 401 && router.currentRoute.value.name !== 'login') {
      router.replace({ name: 'login', query: { redirect: router.currentRoute.value.fullPath } })
    } else if (!(err.config as AxiosRequestConfig)?.silent && status !== 401) {
      message.error(msg)
    }
    err.friendly = msg
    return Promise.reject(err)
  }
)

/** 取响应数据。 */
export const unwrap = <T = any>(p: Promise<{ data: T }>): Promise<T> => p.then((r) => r.data)
