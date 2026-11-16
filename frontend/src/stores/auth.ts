import { defineStore } from 'pinia'
import { authApi, metaApi } from '@/api'

/** 当前登录用户与平台元信息。 */
export const useAuthStore = defineStore('auth', {
  state: () => ({ user: null as any, meta: null as any, loaded: false }),
  getters: {
    isAdmin: (s) => s.user?.role === 'Admin',
    landingPath: (s) => s.user?.role !== 'Admin' && s.user?.defaultWorkspaceId ? `/w/${s.user.defaultWorkspaceId}` : '/app',
    displayName: (s) => s.user?.displayName || s.user?.username || ''
  },
  actions: {
    /** 拉取当前用户（未登录返回 false）。 */
    clearIdentity() { this.user = null; this.meta = null; this.loaded = true },
    async fetchMe() {
      try {
        this.user = await authApi.me()
        try { this.meta = await metaApi.get() } catch { /* Keep authenticated identity during metadata outages. */ }
        this.loaded = true
        return true
      } catch (error: any) {
        if (error?.response?.status === 401) this.clearIdentity()
        this.loaded = true
        return !!this.user
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
      try { await authApi.logout() } finally { this.clearIdentity() }
    }
  }
})
