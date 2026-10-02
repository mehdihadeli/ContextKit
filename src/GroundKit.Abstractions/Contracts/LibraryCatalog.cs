namespace GroundKit.Core.Contracts;

public sealed record LibraryCatalogEntry(
    string Name,
    string Registry,
    string Description,
    string Repository,
    string DocsPath
);

public static class LibraryCatalog
{
    public static IReadOnlyList<LibraryCatalogEntry> StarterLibraries { get; } =
        [
            new(
                "react",
                "npm",
                "User interface library",
                "https://github.com/reactjs/react.dev",
                "src/content"
            ),
            new(
                "next",
                "npm",
                "React framework for the web",
                "https://github.com/vercel/next.js",
                "docs"
            ),
            new(
                "typescript",
                "npm",
                "Typed JavaScript language",
                "https://github.com/microsoft/TypeScript-Website",
                "packages/documentation/copy"
            ),
            new("vite", "npm", "Frontend build tool", "https://github.com/vitejs/vite", "docs"),
            new(
                "vue",
                "npm",
                "Progressive JavaScript framework",
                "https://github.com/vuejs/docs",
                "src"
            ),
            new(
                "angular",
                "npm",
                "Web application framework",
                "https://github.com/angular/angular",
                "adev/src/content"
            ),
            new(
                "express",
                "npm",
                "Node.js web framework",
                "https://github.com/expressjs/expressjs.com",
                "en"
            ),
            new(
                "fastify",
                "npm",
                "Fast Node.js web framework",
                "https://github.com/fastify/website",
                "docs"
            ),
            new(
                "nestjs",
                "npm",
                "Node.js server framework",
                "https://github.com/nestjs/docs.nestjs.com",
                "content"
            ),
            new(
                "prisma",
                "npm",
                "Type-safe database toolkit",
                "https://github.com/prisma/docs",
                "content"
            ),
            new(
                "zod",
                "npm",
                "TypeScript-first schema validation",
                "https://github.com/colinhacks/zod",
                "packages/docs/content"
            ),
            new(
                "axios",
                "npm",
                "Promise-based HTTP client",
                "https://github.com/axios/axios-docs",
                "docs"
            ),
            new(
                "graphql",
                "npm",
                "Query language and runtime",
                "https://github.com/graphql/graphql.github.io",
                "src"
            ),
            new(
                "tailwindcss",
                "npm",
                "Utility-first CSS framework",
                "https://github.com/tailwindlabs/tailwindcss.com",
                "src/docs"
            ),
            new(
                "playwright",
                "npm",
                "Browser automation library",
                "https://github.com/microsoft/playwright.dev",
                "nodejs/docs"
            ),
            new(
                "vitest",
                "npm",
                "Vite-native testing framework",
                "https://github.com/vitest-dev/vitest",
                "docs"
            ),
            new(
                "jest",
                "npm",
                "JavaScript testing framework",
                "https://github.com/jestjs/jest",
                "docs"
            ),
            new(
                "eslint",
                "npm",
                "JavaScript code linting",
                "https://github.com/eslint/eslint.org",
                "src/content"
            ),
            new(
                "prettier",
                "npm",
                "Opinionated code formatter",
                "https://github.com/prettier/prettier.com",
                "src/content"
            ),
            new(
                "vitepress",
                "npm",
                "Vite-powered documentation site generator",
                "https://github.com/vuejs/vitepress",
                "docs"
            ),
        ];

    public static LibraryCatalogEntry? Find(string name) =>
        StarterLibraries.FirstOrDefault(entry =>
            entry.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
        );
}
