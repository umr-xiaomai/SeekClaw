<template>
  <a :href="downloadUrl" target="_blank" rel="noopener noreferrer" class="latest-release-link">
    <Download :size="15" />
    <span class="lr-label">{{ isEn ? 'Latest' : '最新版本' }}</span>
    <span class="lr-version">{{ version }}</span>
    <ArrowUpRight :size="13" />
  </a>
</template>

<script setup>
import { computed, onMounted, ref } from 'vue'
import { useData } from 'vitepress'
import { ArrowUpRight, Download } from 'lucide-vue-next'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))

// releases/latest redirects to the newest tag even when the API is rate-limited,
// so the download link always lands on the correct release.
const RELEASES_URL = 'https://github.com/umr-xiaomai/SeekClaw/releases/latest'
const LATEST_API = 'https://api.github.com/repos/umr-xiaomai/SeekClaw/releases/latest'
// Known-good fallback shown before/without the live GitHub check (kept in sync
// with the newest release; the live check below refreshes it automatically).
const FALLBACK_VERSION = 'v1.1.6.0'

const version = ref(FALLBACK_VERSION)
const downloadUrl = ref(RELEASES_URL)

onMounted(async () => {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), 8000)
  try {
    const response = await fetch(LATEST_API, { signal: controller.signal })
    if (!response.ok) throw new Error(String(response.status))
    const release = await response.json()
    if (release?.tag_name || release?.name) {
      // Prefer the human-facing release title (e.g. "v1.1.6.0"); fall back to the
      // tag (e.g. "desktop-v1.1.0.6") and normalize the release prefix away.
      version.value = String(release.name || release.tag_name)
        .trim()
        .replace(/^desktop-?/i, '')
        .replace(/^v/i, 'v')
      const assets = Array.isArray(release.assets) ? release.assets : []
      const setup = assets.find((asset) => /setup/i.test(asset.name ?? '') && /\.exe$/i.test(asset.name ?? ''))
      const archive = assets.find((asset) => /\.zip$/i.test(asset.name ?? ''))
      downloadUrl.value = (setup || archive)?.browser_download_url || release.html_url || RELEASES_URL
    }
  } catch {
    // Keep the fallback: the link still opens the releases page.
  } finally {
    clearTimeout(timer)
  }
})
</script>

<style scoped>
.latest-release-link {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0 1.1rem;
  border: 1px solid var(--seek-card-border);
  border-radius: 10px;
  background: var(--seek-card-bg);
  color: var(--seek-text-primary) !important;
  font-size: 0.9rem;
  font-weight: 650;
  text-decoration: none;
  transition: border-color 0.18s ease, color 0.18s ease, background 0.18s ease;
}

.latest-release-link:hover {
  border-color: color-mix(in srgb, var(--vp-c-brand-1) 55%, var(--seek-card-border));
  color: var(--vp-c-brand-1) !important;
}

.lr-label {
  color: var(--seek-text-secondary);
  font-weight: 500;
}

.lr-version {
  font-variant-numeric: tabular-nums;
}
</style>
