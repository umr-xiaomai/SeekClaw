import { installWebPolyfills } from './web-polyfills'

installWebPolyfills()

import { createApp } from 'vue'
import './styles.css'
import WebRoot from './WebRoot.vue'

createApp(WebRoot).mount('#app')
