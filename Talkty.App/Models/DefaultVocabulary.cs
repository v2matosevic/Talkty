namespace Talkty.App.Models;

/// <summary>
/// Provides default vocabulary configuration for coding and technology domains.
/// Two-layer approach:
/// 1. Prompt context — natural sentences passed to Whisper's initial_prompt to bias the decoder
/// 2. Text replacements — deterministic find/replace after transcription for acoustically ambiguous words
/// </summary>
public static class DefaultVocabulary
{
    /// <summary>
    /// Contextual prompt for Whisper's initial_prompt. Written as natural sentences
    /// rather than a word list — Whisper's decoder responds much better to context
    /// that reads like real speech. Keep under ~200 tokens for best results.
    /// </summary>
    public static string PromptContext { get; } =
        "Claude is an AI assistant by Anthropic. " +
        "I'm coding with React, Vue, Next.js, TypeScript, and Node.js. " +
        "Using kubectl for Kubernetes, Docker, Terraform, and nginx. " +
        "The backend uses PostgreSQL, Redis, GraphQL, FastAPI, and Express. " +
        "Deploying to AWS, Vercel, and Cloudflare with CI/CD via GitHub Actions. " +
        "Tools include VS Code, ESLint, Prettier, Jest, and Playwright.";

    /// <summary>
    /// Post-transcription text replacements for words that Whisper consistently misrecognizes.
    /// Key = what Whisper produces (case-insensitive match), Value = correct replacement.
    /// These are applied deterministically after transcription — 100% reliable.
    /// Only include words where the misrecognition is consistent and the replacement is unambiguous.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultReplacements { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // AI & LLM — these are the most commonly misheard
            ["cloud"] = "Claude",
            ["claud"] = "Claude",
            ["claw'd"] = "Claude",
            ["claude ai"] = "Claude AI",
            ["anthropik"] = "Anthropic",
            ["chat gpt"] = "ChatGPT",
            ["chatgbt"] = "ChatGPT",
            ["open ai"] = "OpenAI",
            ["olama"] = "Ollama",
            ["o llama"] = "Ollama",
            ["lang chain"] = "LangChain",

            // Frameworks & tools commonly misheard
            ["cube cuddle"] = "kubectl",
            ["cube CTL"] = "kubectl",
            ["cube control"] = "kubectl",
            ["kube CTL"] = "kubectl",
            ["post gress"] = "PostgreSQL",
            ["post gres"] = "PostgreSQL",
            ["postgres QL"] = "PostgreSQL",
            ["post gray SQL"] = "PostgreSQL",
            ["mongo db"] = "MongoDB",
            ["redis"] = "Redis",
            ["express js"] = "Express.js",
            ["next js"] = "Next.js",
            ["node js"] = "Node.js",
            ["vue js"] = "Vue.js",
            ["fast api"] = "FastAPI",
            ["graph QL"] = "GraphQL",
            ["web socket"] = "WebSocket",
            ["type script"] = "TypeScript",
            ["java script"] = "JavaScript",

            // DevOps
            ["docker file"] = "Dockerfile",
            ["engine X"] = "nginx",
            ["engine ex"] = "nginx",
            ["terraform"] = "Terraform",
            ["kubernetes"] = "Kubernetes",
            ["github"] = "GitHub",
            ["git lab"] = "GitLab",
            ["VS code"] = "VS Code",

            // Acronyms that get expanded or mangled
            ["eye dee ee"] = "IDE",
            ["see eye dee"] = "CI/CD",
            ["jay son"] = "JSON",
            ["ya ml"] = "YAML",
            ["gee RPC"] = "gRPC",
            ["sequel"] = "SQL",
        };

    /// <summary>
    /// Curated list of coding and technology terms for the vocabulary prompt.
    /// These are joined into the contextual prompt and also stored in settings
    /// for user customization.
    /// </summary>
    public static IReadOnlyList<string> CodingTerms { get; } =
    [
        // AI & LLM
        "Claude", "Anthropic", "OpenAI", "GPT", "LLM", "Ollama", "Whisper",
        "Copilot", "ChatGPT", "Gemini", "LangChain", "RAG", "embeddings",

        // Frontend frameworks
        "React", "Vue", "Angular", "Svelte", "Next.js", "Nuxt", "Astro",
        "Tailwind", "Bootstrap", "Storybook", "Vite", "webpack",
        "useState", "useEffect", "querySelector", "JSX", "TSX",

        // Backend frameworks
        "Express", "NestJS", "FastAPI", "Django", "Flask", "Spring Boot",
        "ASP.NET", "middleware", "REST API", "GraphQL", "gRPC",
        "WebSocket", "OAuth", "JWT",

        // Languages & runtimes
        "TypeScript", "JavaScript", "Python", "Kotlin", "Rust", "Go",
        "Swift", "C#", ".NET", "Node.js", "Deno", "Bun",
        "async", "await", "boolean", "parseInt", "instanceof",

        // DevOps & infrastructure
        "Docker", "Kubernetes", "kubectl", "Terraform", "Ansible",
        "nginx", "Apache", "CI/CD", "GitHub Actions", "GitLab",
        "Dockerfile", "Helm", "Istio",

        // Cloud platforms
        "AWS", "Azure", "GCP", "Vercel", "Netlify", "Cloudflare",
        "Supabase", "Firebase", "Heroku",

        // Databases
        "PostgreSQL", "MongoDB", "Redis", "Elasticsearch",
        "SQLite", "MySQL", "Prisma", "Drizzle",
        "SQL", "NoSQL", "ORM", "CRUD",

        // Messaging & streaming
        "RabbitMQ", "Kafka", "Protocol Buffers",

        // Tools & editors
        "VS Code", "IntelliJ", "Neovim", "Figma",
        "GitHub", "npm", "yarn", "pnpm", "pip", "cargo", "NuGet",
        "ESLint", "Prettier", "Playwright", "Cypress", "Jest", "Vitest",

        // Formats & protocols
        "JSON", "YAML", "TOML", "regex", "HTTP", "HTTPS", "SSH",
        "API endpoint", "localhost", "WebAssembly", "WASM",

        // Concepts & acronyms
        "CLI", "SDK", "IDE", "API", "MVVM", "WPF", "XAML",
        "ARM", "x64", "x86", "CUDA", "Vulkan",

        // OS & platforms
        "Ubuntu", "Debian", "Arch", "Fedora",
        "macOS", "Windows", "Linux", "Homebrew"
    ];
}
