import { defineStore } from 'pinia'
import { authApi, metaApi } from '@/api'

/** 当前登录用户与平台元信息。 */
export const useAuthStore = defineStore('auth', {
  state: () => ({ user: null as any, meta: null as any, loaded: false }),
  getters: {
    isAdmin: (s) => s.user?.role === 'Admin',
    displayName: (s) => s.user?.displayName || s.user?.username || ''
  },
  actions: {
    /** 拉取当前用户（未登录返回 false）。 */
    async fetchMe() {
      try {
        this.user = await authApi.me()
        this.meta = await metaApi.get()
        this.loaded = true
        return true
      } catch {
        this.user = null
        this.loaded = true
        return false
      }
    },
    /** 登录。 */
    async login(username: string, password: string) {
      this.user = await authApi.login(username, password)
      this.meta = await metaApi.get()
      this.loaded = true
    },
    /** 退出登录。 */
    async logout() {
      try { await authApi.logout() } finally { this.user = null }
    }
  }
})
