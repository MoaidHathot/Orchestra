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
├── src/                          # Core library source (3 projects)
│   ├── Orchestra.Engine/         # Core orchestration engine (class library)
│   ├── Orchestra.Host/           # Hosting/API layer (class library)
│   └── Orchestra.Copilot/        # Copilot integration (class library)
├── tests/                        # Test projects (5 total)
│   ├── Orchestra.Engine.Tests/   # Unit — core engine
│   ├── Orchestra.Host.Tests/     # Unit — hosting/API
│   ├── Orchestra.Copilot.Tests/  # Unit — Copilot integration
│   ├── Orchestra.Terminal.Tests/ # Unit — terminal UI
│   └── Orchestra.Portal.Tests/   # Unit — portal
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

- **Orchestra.Engine** — the central orchestration runtime: pipeline definitions, step execution, scheduling (Cronos), and state management.
- **Orchestra.Host** — the ASP.NET Core hosting layer; exposes HTTP APIs and wires DI/lifetime management.
- **Orchestra.Copilot** — thin adapter between the GitHub Copilot SDK and the orchestration engine.
- **Playground** — three runnable surfaces (Console, Terminal, Portal) for experimentation; not intended for production deployment.

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
# Restore and build all projects
dotnet restore
dotnet build OrchestrationEngine.slnx

# Run the unit test suite
dotnet test OrchestrationEngine.slnx

# Run a specific playground surface
dotnet run --project playground/Hosting/Orchestra.Playground.Copilot.Portal
```

### Frontend (Portal)

```bash
cd playground/Hosting/Orchestra.Playground.Copilot.Portal
npm install
npm run dev      # Development server (Vite)
npm run build    # Production bundle
```

### Declarative Orchestration

JSON orchestration definitions live in `examples/`. Use them as templates for defining pipelines without writing C#:

```bash
ls examples/
```

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
