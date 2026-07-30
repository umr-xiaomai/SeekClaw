<template>
  <div class="custom-home-page">
    <HomeHero />

    <div class="main-content-wrapper">
      <HomeCapabilities />
      <DesktopShowcase />
      <ArchitectureOverview />
    </div>
  </div>
</template>

<script setup>
import HomeHero from './HomeHero.vue'
import HomeCapabilities from './HomeCapabilities.vue'
import DesktopShowcase from './DesktopShowcase.vue'
import ArchitectureOverview from './ArchitectureOverview.vue'
</script>

<style scoped>
.custom-home-page {
  width: 100%;
  overflow: hidden;
}

.main-content-wrapper {
  width: min(1200px, calc(100% - 48px));
  margin: 0 auto;
  padding-bottom: 7rem;
}

@media (max-width: 640px) {
  .main-content-wrapper {
    width: min(100% - 32px, 1200px);
    padding-bottom: 4rem;
  }
}
</style>
