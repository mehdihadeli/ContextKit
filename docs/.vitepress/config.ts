import { defineConfig } from "vitepress";

export default defineConfig({
  title: "ContextKit",
  description: "Local-first documentation grounding for AI agents",
  base: process.env.GITHUB_ACTIONS
    ? `/${process.env.GITHUB_REPOSITORY?.split("/")[1] ?? "groundkit"}/`
    : "/",
  cleanUrls: true,
  appearance: true,
  lastUpdated: true,
  themeConfig: {
    logo: "/mark.svg",
    nav: [
      { text: "Guide", link: "/guide/quickstart" },
      { text: "Libraries", link: "/libraries" },
      { text: "Features", link: "/features" },
    ],
    sidebar: {
      "/guide/": [
        {
          text: "Start here",
          items: [
            { text: "Quickstart", link: "/guide/quickstart" },
            { text: "Architecture", link: "/guide/architecture" },
            { text: "Grounding model", link: "/guide/grounding" },
            { text: "Search modes", link: "/guide/grounding#choosing-a-search-mode" },
            { text: "Release versioning", link: "/guide/versioning" },
          ],
        },
        {
          text: "Connect an agent",
          items: [
            { text: "MCP setup", link: "/guide/mcp" },
            { text: "Question-based search", link: "/guide/question-search" },
            { text: "Package workflow", link: "/guide/packages" },
            { text: "Integrations", link: "/integrations" },
          ],
        },
        {
          text: "Reference",
          items: [
            { text: "CLI", link: "/reference/cli" },
            { text: "MCP tools", link: "/reference/mcp-tools" },
            { text: "Registry", link: "/reference/registry" },
          ],
        },
      ],
      "/reference/": [
        {
          text: "Reference",
          items: [
            { text: "CLI", link: "/reference/cli" },
            { text: "MCP tools", link: "/reference/mcp-tools" },
            { text: "Registry", link: "/reference/registry" },
          ],
        },
      ],
    },
    outline: "deep",
    outlineTitle: "On this page",
    sidebarMenuLabel: "Menu",
    returnToTopLabel: "Return to top",
    darkModeSwitchLabel: "Appearance",
    lightModeSwitchTitle: "Switch to light theme",
    darkModeSwitchTitle: "Switch to dark theme",
    search: { provider: "local" },
    footer: {
      message: "Build once. Query locally. Ground with sources.",
      copyright: "ContextKit",
    },
  },
});
