<template>
  <div class="custom-home-page">
    <!-- 1. Hero Section -->
    <HomeHero />

    <div class="main-content-wrapper">
      <!-- 2. Terminal Live Demo -->
      <div class="section-block">
        <div class="section-tag">
          {{ isEn ? 'LIVE INTERACTIVE DEMO' : '⚡ 实时交互体验' }}
        </div>
        <HeroTerminal />
      </div>

      <!-- 3. Multi Provider & Routing Matrix -->
      <div class="section-block">
        <ProviderGrid />
      </div>

      <!-- 4. Clean Architecture Overview -->
      <div class="section-block">
        <ArchitectureOverview />
      </div>

      <!-- 5. Interactive Code & Usage Showcase -->
      <div class="section-block">
        <HomeCodeShowcase />
      </div>
    </div>
  </div>
</template>

<script setup>
import { computed } from 'vue'
import { useData } from 'vitepress'

import HomeHero from './HomeHero.vue'
import HeroTerminal from './HeroTerminal.vue'
import ProviderGrid from './ProviderGrid.vue'
import ArchitectureOverview from './ArchitectureOverview.vue'
import HomeCodeShowcase from './HomeCodeShowcase.vue'

const { lang } = useData()
const isEn = computed(() => lang.value === 'en-US' || lang.value?.startsWith('en'))
</script>

<style scoped>
.custom-home-page {
  width: 100%;
}

.main-content-wrapper {
  max-width: 1120px;
  margin: 0 auto;
  padding: 0 24px 64px 24px;
}

.section-block {
  margin-bottom: 3.5rem;
}

.section-tag {
  font-size: 0.8rem;
  font-weight: 700;
  text-transform: uppercase;
  color: var(--vp-c-brand-1);
  letter-spacing: 0.05em;
  margin-bottom: 0.5rem;
}
</style>
