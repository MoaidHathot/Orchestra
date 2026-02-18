# AI Agent Loops - Research Brief

## Definition

AI Agent Loops are autonomous systems where LLMs direct their own processes and tool usage in iterative cycles: Observe → Plan → Execute → Evaluate → Repeat. They use tools (file systems, APIs, code execution) based on environmental feedback and maintain memory across interactions.

**Architectural Patterns (Anthropic):**
| Pattern | Description |
|---------|-------------|
| Prompt Chaining | Sequential LLM calls where each output feeds the next |
| Routing | Classifies inputs to specialized handlers |
| Parallelization | Simultaneous task execution |
| Orchestrator-Workers | Central LLM delegates to worker LLMs |
| Evaluator-Optimizer | One LLM generates, another critiques in a loop |

## Timeline (2024-2026)

**2024:**
- Andrew Ng popularized "agentic AI" terminology
- Anthropic released Model Context Protocol (MCP)
- Claude Code launched with terminal/IDE/browser integration

**2025:**
- OpenAI Operator and ChatGPT Deep Research launched (January)
- Manus AI demonstrated autonomous web browsing
- Hugging Face released Open Deep Research
- Linux Foundation announced Agentic AI Foundation (December)
- Communication protocols launched: Agent2Agent (Google), Agent Network Protocol

**2026:**
- Claude Code available on Terminal, VS Code, JetBrains, Desktop, Web, Mobile
- Claude Agent SDK and AWS Strands Agents SDK in production
- GitHub Actions/GitLab CI/CD integration for automated PR reviews

## Key Players

| Organization | Technology | Focus |
|--------------|------------|-------|
| Anthropic | Claude Code, Agent SDK, MCP | Coding agents, tool protocols |
| OpenAI | Operator, Deep Research, Codex | Web automation, research |
| Google | Agent2Agent, SIMA | Multi-agent communication |
| Microsoft | AutoGen, Copilot | Enterprise agents |
| AWS | Strands Agents SDK | Cloud-deployed agents |
| Cognition AI | Devin AI | Autonomous software engineer |
| LangChain | LangGraph | Agent frameworks |

## Facts vs. Misconceptions

| Misconception | Reality |
|---------------|---------|
| "More complexity = better" | Anthropic: "Start with simple prompts. Add agentic systems only when simpler solutions fall short." |
| "Agents are fully autonomous" | Current agents operate at Level 2-3 autonomy (Financial Times analogy) |
| "Frameworks are required" | Agent patterns require a few lines of code using direct LLM API calls |
| "Agents replace humans" | Production implementations include human checkpoints and guardrails |
| "One agent handles everything" | Production systems use specialized agents with orchestration |

## Quantitative Data

| Metric | Value | Source |
|--------|-------|--------|
| Enterprise adoption | Experimenting phase | Fortune, June 2025 |
| Production deployments | Few | AP, April 2025 |
| Agent archetypes | 7 categories | The Information, 2025 |
| Claude Code platforms | 6 environments | Anthropic, 2026 |
| Major frameworks | 10+ | Industry analysis |
| Communication protocols | 6+ standards | Wikipedia |

Agentic systems trade latency and cost for task performance.
