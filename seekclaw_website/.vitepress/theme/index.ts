import DefaultTheme from 'vitepress/theme'
import './custom.css'

import HomePage from './components/HomePage.vue'
import HomeHero from './components/HomeHero.vue'
import HomeCodeShowcase from './components/HomeCodeShowcase.vue'
import HeroTerminal from './components/HeroTerminal.vue'
import ProviderGrid from './components/ProviderGrid.vue'
import ArchitectureOverview from './components/ArchitectureOverview.vue'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('HomePage', HomePage)
    app.component('HomeHero', HomeHero)
    app.component('HomeCodeShowcase', HomeCodeShowcase)
    app.component('HeroTerminal', HeroTerminal)
    app.component('ProviderGrid', ProviderGrid)
    app.component('ArchitectureOverview', ArchitectureOverview)
  }
}
