import { defineConfig } from 'vitepress'

const pkgRepo = 'https://github.com/opoi375/com.opoi375.physics-simulation'
const docsRepo = pkgRepo // 文档源文件在包仓库的 Documentation~/ 下

// 侧边栏结构（中英文共用同一套路径，文案在各自 locale 里定义）
function sidebarZh() {
  return [
    {
      text: '指南',
      items: [
        { text: '概述', link: '/guide/overview' },
        { text: '安装', link: '/guide/installation' },
        { text: '快速上手', link: '/guide/quickstart' }
      ]
    },
    {
      text: '模拟模块',
      items: [
        { text: '质点弹簧（Mass-Spring）', link: '/mass-spring/' },
        { text: '布料（Cloth）', link: '/cloth/' },
        { text: '软体（Soft Body）', link: '/soft-body/' },
        { text: '从网格到质点（焊接原理）', link: '/soft-body/mesh-to-particles' },
        { text: '真实模型实测', link: '/soft-body/model-audit' },
        { text: '碰撞代理（Collision）', link: '/collision/' }
      ]
    },
    {
      text: '工具',
      items: [{ text: '编辑器工具一览', link: '/tools/' }]
    },
    {
      text: '参考',
      items: [
        { text: '质点弹簧参数参考', link: '/reference/mass-spring-parameters' },
        { text: '布料参数参考', link: '/reference/cloth-parameters' },
        { text: '软体参数参考', link: '/reference/soft-body-parameters' },
        { text: '更新日志', link: '/changelog' }
      ]
    }
  ]
}

function sidebarEn() {
  return [
    {
      text: 'Guide',
      items: [
        { text: 'Overview', link: '/en/guide/overview' },
        { text: 'Installation', link: '/en/guide/installation' },
        { text: 'Quick Start', link: '/en/guide/quickstart' }
      ]
    },
    {
      text: 'Simulation Modules',
      items: [
        { text: 'Mass-Spring System', link: '/en/mass-spring/' },
        { text: 'Cloth Simulation', link: '/en/cloth/' },
        { text: 'Soft Body Simulation', link: '/en/soft-body/' },
        { text: 'Mesh to Particles (welding)', link: '/en/soft-body/mesh-to-particles' },
        { text: 'Real-Model Audit', link: '/en/soft-body/model-audit' },
        { text: 'Collision Proxies', link: '/en/collision/' }
      ]
    },
    {
      text: 'Tools',
      items: [{ text: 'Editor Tools', link: '/en/tools/' }]
    },
    {
      text: 'Reference',
      items: [
        { text: 'Mass-Spring Parameter Reference', link: '/en/reference/mass-spring-parameters' },
        { text: 'Cloth Parameter Reference', link: '/en/reference/cloth-parameters' },
        { text: 'Soft Body Parameter Reference', link: '/en/reference/soft-body-parameters' },
        { text: 'Changelog', link: '/en/changelog' }
      ]
    }
  ]
}

export default defineConfig({
  base: '/com.opoi375.physics-simulation/',
  title: 'Physics Simulation',
  description: 'Deterministic point-mass & spring physics simulation toolkit for Unity',
  lastUpdated: true,
  cleanUrls: true,

  head: [
    ['link', { rel: 'icon', type: 'image/png', href: '/com.opoi375.physics-simulation/favicon-32.png' }],
    ['link', { rel: 'icon', type: 'image/x-icon', href: '/com.opoi375.physics-simulation/favicon.ico' }]
  ],

  locales: {
    root: {
      label: '简体中文',
      lang: 'zh-CN',
      themeConfig: {
        nav: [
          { text: '指南', link: '/guide/overview' },
          { text: '模拟模块', link: '/collision/' },
          { text: '工具', link: '/tools/' },
          { text: '参考', link: '/reference/mass-spring-parameters' }
        ],
        sidebar: sidebarZh(),
        outline: { level: [2, 3], label: '本页目录' },
        docFooter: { prev: '上一页', next: '下一页' },
        lastUpdatedText: '最后更新',
        returnToTopLabel: '回到顶部',
        sidebarMenuLabel: '菜单',
        darkModeSwitchLabel: '深色模式',
        editLink: {
          pattern: `${docsRepo}/edit/main/Documentation~/:path`,
          text: '在 GitHub 上编辑此页'
        }
      }
    },
    en: {
      label: 'English',
      lang: 'en-US',
      link: '/en/',
      themeConfig: {
        nav: [
          { text: 'Guide', link: '/en/guide/overview' },
          { text: 'Modules', link: '/en/collision/' },
          { text: 'Tools', link: '/en/tools/' },
          { text: 'Reference', link: '/en/reference/mass-spring-parameters' }
        ],
        sidebar: { '/en/': sidebarEn() },
        outline: { level: [2, 3], label: 'On this page' },
        editLink: {
          pattern: `${docsRepo}/edit/main/Documentation~/:path`,
          text: 'Edit this page on GitHub'
        }
      }
    }
  },

  themeConfig: {
    logo: '/logo.png',
    socialLinks: [{ icon: 'github', link: pkgRepo }],
    search: { provider: 'local' }
  }
})
