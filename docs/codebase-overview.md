---
layout: default
title: Codebase Overview
nav_order: 10
---

# Codebase Overview

> **Orchestra** — a .NET 10 AI orchestration engine with Copilot, MCP, and multi-surface hosting.

---

## 1. Project Summary

Orchestra is an open-source AI orchestration framework written in C# targeting .NET 10. It provides a structured pipeline engine for composing, scheduling, and executing AI agent workflows — with first-class integrations for **GitHub Copilot** and the **Model Context Protocol (MCP)**. The project is currently pre-production, deliberately built on the cutting edge of the .NET and AI-SDK ecosystem.

**At a glance:**

| Attribute | Value |
|---|---|
| Language | C# (.NET 10 / `net10.0`) |
| Frontend | React 18 + TypeScript (Vite) |
| Architecture | Layered class libraries + pluggable host surfaces |
| Test strategy | Unit (xUnit + NSubstitute), Integration (ASP.NET Testing), E2E (Playwright) |
| Packaging | Central NuGet versioning (`Directory.Packages.props`) |
| CI/CD | GitHub Actions (`.github/`) |
| Containerisation | Docker |

---

## 2. Architecture & Structure

### Directory Tree

```
Orchestra/
├── src/                          # Source (8 projects)
│   ├── Orchestra.Engine/         # Core orchestration runtime (class library)
│   ├── Orchestra.Host/           # Hosting/API layer, persistence, MCP server
│   ├── Orchestra.Copilot/        # GitHub Copilot provider
│   ├── Orchestra.OpenCode/       # OpenCode provider
│   ├── Orchestra.ProcessHost/    # External process supervision (orchestra.services.json)
│   ├── Orchestra.Client/         # HTTP/SSE client SDK + interactive run stack
│   ├── Orchestra.Cli/            # The packaged `orchestra` dotnet tool
│   └── Orchestra.Server/         # Standalone API-only ASP.NET host
├── tests/                        # Test projects (13 total)
├── templates/                    # Starter orchestrations scaffolded by `orchestra init`
├── schemas/                      # JSON schemas (orchestration, orchestra, mcp, services)
├── skills/                       # Agent Skills, incl. orchestration-authoring
├── playground/
│   └── Hosting/
│       ├── Orchestra.Playground.Copilot/         # Console worker
│       ├── Orchestra.Playground.Copilot.Terminal/ # Terminal UI host
│       └── Orchestra.Playground.Copilot.Portal/   # Portal host
├── examples/                     # Declarative JSON orchestration definitions
├── docs/                         # Jekyll documentation site
├── scripts/                      # Utility/build scripts
├── utils/                        # Shared utilities
├── .github/                      # CI/CD GitHub Actions workflows
├── OrchestrationEngine.slnx      # Solution file
├── Directory.Build.props         # Global MSBuild settings (net10.0)
├── Directory.Packages.props      # Central NuGet versioning
├── nuget.config                  # NuGet feed config
└── .editorconfig                 # Code style enforcement
```

### Layered Design

```
┌─────────────────────────────────────────────┐
│              Playground / Hosts             │
│  Console  │  Terminal  │  Portal           │
├─────────────────────────────────────────────┤
│          Orchestra.Host  (API layer)        │
├──────────────────────┬──────────────────────┤
│  Orchestra.Engine    │  Orchestra.Copilot   │
│  (pipeline core)     │  (Copilot SDK glue)  │
└──────────────────────┴──────────────────────┘
```

- **Orchestra.Engine** - the orchestration runtime: model, parsers, DAG executor, per-type step executors, template resolution, engine tools, agent abstractions, triggers, hooks.
- **Orchestra.Host** - the ASP.NET Core hosting layer: minimal-API endpoint groups, SSE reporters, the MCP server, persistence, retention, and `orchestra.json` loading.
- **Orchestra.Copilot / Orchestra.OpenCode** - the two agent providers, registered keyed so a step's `provider` field is honored.
- **Orchestra.ProcessHost** - supervises the external processes declared in `orchestra.services.json`.
- **Orchestra.Client** - HTTP/SSE client SDK plus the shared interactive run stack (observers, HITL prompters, exit codes).
- **Orchestra.Cli** - the packaged `orchestra` tool: Spectre.Console command app, one-shot exec engine, and the embedded portal.
- **Playground** - three runnable surfaces (Console, Terminal, Portal). The Portal project physically owns the React SPA that `orchestra portal` serves.

For deep-dives on specific subsystems:

- [Orchestration step semantics + template bindings](orchestration-step-deep-dive.md) —
  the `type: Orchestration` step, child run drill-in via templates, self-healing patterns.
- [Run storage reference](run-storage.md) — what `run.json` contains, on-disk layout, and
  how parent → child orchestration links are persisted.

### Entry Points

| Surface | Project | Type |
|---|---|---|
| Console worker | `Orchestra.Playground.Copilot` | `IHostedService` / Console |
| Terminal UI | `Orchestra.Playground.Copilot.Terminal` | `Spectre.Console` TUI |
| Portal | `Orchestra.Playground.Copilot.Portal` | React SPA + ASP.NET Core |

---

## 3. Technology Stack

### Languages & Runtimes

| Layer | Language | Runtime / Toolchain |
|---|---|---|
| Backend | C# | .NET 10 (`net10.0`) |
| Frontend | TypeScript | Node.js / Vite 6 |
| Docs | Markdown / Liquid | Jekyll (GitHub Pages) |
| Infrastructure | YAML | GitHub Actions |

### Frameworks & Libraries

| Category | Technology | Role |
|---|---|---|
| Hosting / DI | `Microsoft.Extensions.Hosting` | Generic host, DI container, lifetime |
| Web | `Microsoft.AspNetCore.App` | HTTP APIs (framework reference) |
| AI / Agents | `GitHub.Copilot.SDK` | Copilot agent integration |
| Protocol | `ModelContextProtocol` | MCP server/client |
| Scheduling | `Cronos` | Cron expression parsing |
| Terminal UI | `Spectre.Console` | Rich TUI rendering |
| Frontend UI | React 18 + `react-dom` | Portal SPA |
| Diagrams | `mermaid` | In-browser graph/diagram rendering |
| Build | Vite + `@vitejs/plugin-react` | Frontend bundling |
| Testing | xUnit, FluentAssertions, NSubstitute | Unit / integration tests |
| E2E | Microsoft Playwright | Browser-based E2E tests |

---

## 4. Dependencies

### Full Dependency Table

| Package | Version | Type | Purpose |
|---|---|---|---|
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.2 | .NET Prod | Logging abstraction (`ILogger`) |
| `Microsoft.Extensions.Hosting` | 10.0.2 | .NET Prod | Generic host / DI / lifetime |
| `Microsoft.Extensions.Http` | 10.0.2 | .NET Prod | `HttpClient` factory |
| `Microsoft.Extensions.Options` | 10.0.2 | .NET Prod | Options pattern (`IOptions<T>`) |
| `GitHub.Copilot.SDK` | 0.1.29 | .NET Prod | Copilot agent integration |
| `ModelContextProtocol` | 0.2.0-preview.2 | .NET Prod | MCP server/client protocol |
| `Cronos` | 0.8.4 | .NET Prod | Cron expression parsing |
| `Spectre.Console` | 0.50.0 | .NET Prod | Rich terminal UI |
| `Microsoft.AspNetCore.App` | — | .NET Prod (framework ref) | ASP.NET Core web framework |
| `react` / `react-dom` | ^18.3.1 | JS Prod | Portal UI framework |
| `mermaid` | ^10.9.3 | JS Prod | Diagram/graph rendering |
| `vite` | ^6.0.7 | JS Dev | Frontend build tool |
| `typescript` | ^5.7.3 | JS Dev | Type-safe JavaScript |
| `@vitejs/plugin-react` | ^4.3.4 | JS Dev | Vite React plugin |
| `xunit` / `xunit.runner.visualstudio` | 2.9.2 / 3.0.0 | .NET Test | Test framework + runner |
| `FluentAssertions` | — | .NET Test | Assertion library |
| `NSubstitute` | — | .NET Test | Mocking framework |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.0-preview.1 | .NET Test | Integration test host |
| `Microsoft.Playwright` / `.Xunit` | — | .NET Test | E2E browser testing |

### Risk Assessment

**Overall risk: Medium-High** — driven primarily by heavy reliance on preview and pre-1.0 software across the stack. All major risks are deliberate trade-offs consistent with building a cutting-edge AI orchestration framework alongside evolving platforms.

| Risk | Severity | Detail |
|---|---|---|
| **`net10.0` preview runtime** | 🔴 High | Entire platform is pre-GA. API surface may shift before the Nov 2025 release. |
| **`GitHub.Copilot.SDK` 0.1.29** | 🔴 High | Pre-1.0; no stability guarantees. Breaking changes are likely as the Copilot platform matures. |
| **`ModelContextProtocol` 0.2.0-preview.2** | 🔴 High | Rapidly evolving spec; SDK API surface expected to change significantly before stable release. |
| **`mermaid` ^10.9.3** | 🟡 Medium | One major version behind (v11.x is stable); migration effort is low but should be tracked. |
| **xunit v2/v3 runner mismatch** | 🟡 Medium | Forward-compatible but misaligned; worth unifying to avoid subtle test runner issues. |
| **`react` ^18.3.1** | 🟢 Low | React 19 is stable but v18 is still actively maintained. Monitor for EOL timeline. |
| **`Microsoft.AspNetCore.Mvc.Testing` preview** | 🟢 Low | Risk is inherited from the .NET 10 preview; no independent concern beyond that. |

> **Reassess when:** .NET 10 reaches GA (Nov 2025) and `GitHub.Copilot.SDK` publishes a stable 1.x release.

---

## 5. Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (preview)
- [Node.js](https://nodejs.org/) ≥ 20 (for the Portal frontend)
- Docker (optional)
- A GitHub Copilot-enabled account / token (for Copilot playground surfaces)

### Build & Run

```bash
# Restore and build everything (src, tests, playground)
dotnet restore OrchestrationEngine.slnx
dotnet build OrchestrationEngine.slnx

# Run the test suite the way CI does (excludes Playwright E2E)
dotnet test OrchestrationEngine.Tests.slnx --filter "Category!=E2E"

# Run the CLI from source
dotnet run --project src/Orchestra.Cli -- doctor

# Pack the tool (and optionally publish)
./pack.ps1
./pack.ps1 -Push -LocalFeed C:\path\to\local-feed
```

Set `SKIP_PORTAL_BUILD=true` to skip the Portal's `npm install` + Vite build, which otherwise
runs whenever `Orchestra.Cli` builds. Useful for fast iteration; unset it before packing.

Two solutions exist deliberately: `OrchestrationEngine.slnx` builds everything, while
`OrchestrationEngine.Tests.slnx` is the test-only solution CI runs.

### Frontend (Portal)

```bash
cd playground/Hosting/Orchestra.Playground.Copilot.Portal
npm install
npm run dev      # Development server (Vite)
npm run build    # Production bundle
```

### Declarative Orchestration

Orchestrations are JSON/YAML - no C# required. The fastest way in is the tool itself:

```bash
dotnet run --project src/Orchestra.Cli -- init ./scratch
dotnet run --project src/Orchestra.Cli -- validate ./examples/hello-world.yaml
```

`examples/` holds ~46 runnable reference orchestrations, `templates/` holds the five starters
`orchestra init` scaffolds. Both are covered by tests that run the real parser and
template-expression validator, so a broken example fails the build.

---

## 6. Recommendations for Improvement

### High Priority

1. **Pin preview dependencies with a GA upgrade plan.** `net10.0`, `GitHub.Copilot.SDK`, and `ModelContextProtocol` are all pre-stable. Document the expected GA dates and assign an owner to track breaking changes. Consider a `DEPENDENCY_ROADMAP.md` in the repo root.

2. **Unify xUnit runner versions.** The mismatch between `xunit` 2.9.2 and `xunit.runner.visualstudio` 3.0.0 can cause subtle test runner inconsistencies. Align on a single major version across all test projects.

3. **Add a `CHANGELOG.md`.** Given the fast-moving dependency surface, a changelog makes it easier to communicate breaking changes to consumers of the library.

### Medium Priority

4. **Upgrade `mermaid` to v11.x.** The jump from v10 to v11 is low-effort and brings performance improvements and new diagram types relevant to orchestration graph visualisation.

5. **Introduce architecture decision records (ADRs).** The project makes several bold technology choices (MCP, Copilot SDK, net10). Capturing *why* in `docs/adr/` helps future maintainers avoid re-litigating decisions.

6. **Add a health-check endpoint to the Portal playground.** This makes CI smoke-tests more reliable (`/healthz` via `Microsoft.AspNetCore.Diagnostics.HealthChecks`).

### Low Priority / Nice-to-Have

7. **Upgrade React to v19.** React 18 is still maintained, but v19 is stable and brings performance improvements and improved server-component support that may benefit the Portal.

8. **Consider a `docker-compose.yml`** that brings up the Portal playground together with any MCP servers, lowering the barrier for local end-to-end development.
