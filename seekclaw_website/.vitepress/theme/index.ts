import DefaultTheme from 'vitepress/theme'
import './custom.css'

import HomePage from './components/HomePage.vue'
import LatestRelease from './components/LatestRelease.vue'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('HomePage', HomePage)
    app.component('LatestRelease', LatestRelease)
  }
}
