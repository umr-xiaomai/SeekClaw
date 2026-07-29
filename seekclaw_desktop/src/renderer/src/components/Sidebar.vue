<script setup lang="ts">
import {
  Blocks,
  CircleHelp,
  Clock3,
  Folder,
  GitPullRequest,
  MessageSquareText,
  Plus,
  Search,
  Settings2,
  SquarePen
} from '@lucide/vue'
import { computed, ref } from 'vue'
import logoUrl from '../../../../resources/logo.svg?url'
import type { ProjectItem, ThreadItem } from '../types'

const props = defineProps<{
  projects: ProjectItem[]
  threads: ThreadItem[]
  activeThreadId: string
  version: string
}>()

const emit = defineEmits<{
  newTask: []
  openWorkspace: []
  selectThread: [id: string]
  selectProject: [project: ProjectItem]
  openExtensions: []
  openSettings: []
}>()

const searching = ref(false)
const query = ref('')
const filteredThreads = computed(() => {
  const value = query.value.trim().toLocaleLowerCase()
  return value ? props.threads.filter((thread) => thread.title.toLocaleLowerCase().includes(value)) : props.threads
})
</script>

<template>
  <aside class="sidebar">
    <div class="sidebar-brand-row">
      <div class="brand-lockup">
        <img :src="logoUrl" alt="" />
        <span>SeekClaw</span>
      </div>
      <button class="icon-button" title="搜索任务" @click="searching = !searching">
        <Search :size="18" />
      </button>
    </div>

    <div v-if="searching" class="sidebar-search">
      <Search :size="15" />
      <input v-model="query" autofocus placeholder="搜索任务" />
    </div>

    <nav class="primary-nav">
      <button class="nav-item is-primary" @click="emit('newTask')">
        <SquarePen :size="19" />
        <span>新建任务</span>
      </button>
      <button class="nav-item" disabled title="即将支持">
        <GitPullRequest :size="19" />
        <span>拉取请求</span>
      </button>
      <button class="nav-item" disabled title="即将支持">
        <Clock3 :size="19" />
        <span>已安排</span>
      </button>
      <button class="nav-item" @click="emit('openExtensions')">
        <Blocks :size="19" />
        <span>MCP 与技能</span>
      </button>
    </nav>

    <section class="sidebar-section">
      <div class="section-heading">
        <span>项目</span>
        <button class="icon-button compact" title="添加项目" @click="emit('openWorkspace')">
          <Plus :size="15" />
        </button>
      </div>
      <button
        v-for="project in projects"
        :key="project.id"
        class="project-row"
        :title="project.path"
        @click="emit('selectProject', project)"
      >
        <Folder :size="18" />
        <span>{{ project.name }}</span>
      </button>
    </section>

    <section class="sidebar-section recent-section">
      <div class="section-heading"><span>最近</span></div>
      <button
        v-for="thread in filteredThreads"
        :key="thread.id"
        class="thread-row"
        :class="{ active: thread.id === activeThreadId }"
        @click="emit('selectThread', thread.id)"
      >
        <MessageSquareText :size="16" />
        <span>{{ thread.title }}</span>
      </button>
      <p v-if="filteredThreads.length === 0" class="empty-recent">暂无任务</p>
    </section>

    <footer class="sidebar-footer">
      <button class="account-row" @click="emit('openSettings')">
        <span class="account-avatar">S</span>
        <span class="account-copy">
          <strong>SeekClaw Desktop</strong>
          <small>v{{ version }}</small>
        </span>
        <Settings2 :size="17" />
      </button>
      <a class="icon-button" href="https://seekclaw.hoilai.com/doc/" target="_blank" title="帮助" rel="noreferrer">
        <CircleHelp :size="18" />
      </a>
    </footer>
  </aside>
</template>
