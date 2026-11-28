# Glacier.Studio User Manual

`Glacier.Studio` is a native desktop coding IDE and inference control panel arranged into three main panes: **Threads & Projects** (left), **Agent & Chat Canvas** (center), and the **Inspector & Runtime Drawer** (right).

## 1. Workspace Layout & Session Controls

### Left Sidebar (Threads & Projects)
- **New Thread / Delete / Filter:** Click `+ New Thread` to start an empty conversation. Use `Filter threads...` to search thread titles and message bodies.
- **Persistence & Export:** Threads, preferences (`studio-prefs.json`), and exports live under `<workspace>/.glacier/studio/sessions/` and `<workspace>/.glacier/studio/exports/`. Export any conversation as Markdown (`.md`) or JSON (`.json`).
- **Projects:** Click `+` beside **Projects** (or click the bottom-left workspace pill) to select or switch active repository directories. Removing a non-active project only removes it from your recent list in `studio-prefs.json`.
- **UI Zoom:** Step UI zoom between `75%` and `175%`, or click the percentage button to reset to `100%`.

---

## 2. Slash Commands & Agent Tools

### Slash Commands

Type any command directly in the chat box:

| Command | Action |
| :--- | :--- |
| `/help` | Prints the available studio slash commands in chat |
| `/team <objective>` | Runs a coordinated team workflow across configured Host and remote nodes |
| `/terminal <command>` | Switches to the Terminal tab and executes `<command>` directly in PowerShell/shell |
| `/inspect <filepath>` | Loads `<filepath>` into the Document tab (`Glacier.DocTree` outline preview) |
| `/graph [entity]` | Queries the in-process `Glacier.Graph` symbol neighborhood for `[entity]` (defaults to `Workspace`) |
| `/mcp` | Lists all configured MCP servers from `~/.gemini/config/mcp_config.json` and their discovered tools |
| `/clear`, `/reset` | Clears messages and resets `StudioWorkingMemory` in the current thread |
| `/status` | Prints active session ID, selected model, provider, message count, MCP count, and agent mode |
| `/plan <objective>` | Builds a multi-step engineering plan before modifying files |
| `/goal <objective>` | Executes a multi-turn goal-directed ReAct loop |

### Built-In Agent Tools

When **Agent Mode** (`IsAgentMode = true`) is enabled, the active model can invoke workspace, knowledge-graph, MCP, shell, and web tools:

| Tool | Required / Main Arguments | Behavior |
| :--- | :--- | :--- |
| `run_command` | `{"command": "..."}` | Executes a PowerShell/shell command in `GlacierConsoleService` and streams output |
| `read_file` | `{"path": "..."}` | Reads a workspace file (truncated past 8,000 chars), updates the Document tab, and indexes symbols |
| `write_file` | `{"path": "...", "content": "..."}` | Writes or creates a workspace file, updates DocTree preview, and re-indexes AST symbols |
| `delete_file` | `{"path": "..."}` | Deletes a file inside the workspace root |
| `list_directory` | `{"path": "..."}` | Lists files and subdirectories (`""` for workspace root) |
| `search_workspace` | `{"query": "...", "scope": "..."}` | Searches workspace files via `GlacierWorkspaceGraphService` |
| `query_knowledge_graph` | `{"entity": "..."}` | Queries symbol and project dependencies in `Glacier.Graph` (`maxEdges: 30`) |
| `inspect_document` | `{"path": "..."}` | Opens a file in the right **Document** tab and reports word/outline counts |
| `call_mcp_tool` | `{"server": "...", "tool": "...", "arguments": {...}}` | Invokes a tool on a connected stdio MCP server |
| `fetch_web_url` | `{"url": "..."}` | Fetches an HTTP URL, strips HTML, and returns clean readable text |
| `search_web` | `{"query": "..."}` | Runs web/news search and returns summarized matches |

All file operations validate resolved paths against the active workspace boundary (`TryResolveWorkspacePath`).

---

## 3. Inspector & Runtime Drawer (Right Pane)

1. **Document (`doc`):** Displays the active Markdown or source file parsed by `Glacier.DocTree`, showing live word count, estimated token count, interactive outline headings with 1-based line click-to-scroll, and a **Preview / Raw** toggle.
2. **Terminal (`console`):** Persistent shared PowerShell/shell console tied to the active project working directory.
3. **Graph (`graph`):** Inspects the in-memory `Glacier.Graph` AST index of projects, files, types, and methods.
4. **System / Runtimes (`telemetry`):** Launch and inspect inference backends:
   - **GGUF Discovery:** Automatically scans `$GLACIER_MODELS_DIR`, `~/.glacier/models`, `$LMSTUDIO_MODELS_DIR`, `~/.lmstudio/models`, `<workspace>/Glacier.Inference/models`, `<workspace>/models`, and `<workspace>/*.gguf`. Models sized within the detected discrete VRAM budget (`StandaloneVramBudgetGb`, defaulting to `75%` of discrete VRAM or `4.8 GB` fallback) are prioritized for single-node local startup.
   - **Single-Node `Glacier.Inference` (`glacier-ai` on `:5055`):** Configure `--device`, `--engine` (`baremetal`, `cuda`, `vulkan`, `directcompute`, `cpu`), `--port`, `--ctx`, and optional Sleep Memory (`sleep.gmoe` checkpoint). `GetInferenceExePath()` resolves `glacier-ai.exe` (or `glacier-ai`) first and falls back to legacy `glacier.exe` / `glacier` if built previously.
   - **Sleep Memory Card:** Refreshes `/api/sleep/status` telemetry and triggers consolidation manually (`Trigger Sleep`).
   - **Multi-Node `Glacier.Cluster / Serve` (`glacier-cluster` on `:8080` / `:5056`):** Switch between `coordinator` (`--stages`, `--layers`, `--host-gpu`, `--host`, `--http-port`, `--cache`, `--tokens`) and `worker` (`--port`, `--cache`), apply presets (`Local :5056`, `2x Local`, `3-Stage`, `Host Only`), view live `/v1/cluster/status` stage layer ranges, and inspect the exact generated CLI preview.
   - **AMD XDNA 2 NPU Sidecar:** Probes FastFlowLM / Lemonade / custom OpenAI-compatible sidecars and displays shared UMA cap telemetry.

---

## 4. MCP Integration & Secret Storage

- **MCP Servers (`GlacierMcpManager`):** Reads `~/.gemini/config/mcp_config.json` (`mcpServers` object with `command`, `args`, `env`, `disabled`, `disabledTools`). If no config file is present, built-in ecosystem presets (`GlacierVectorEngine`, `GlacierGraphEngine`, `SharpTools`) appear in the list.
- **API Keys & Secrets (`StudioSecretProtector`):** Reads `GEMINI_API_KEY` from environment variables on startup when set. Encrypted local secrets in `<workspace>/.glacier/studio/secrets.enc.json` use Windows DPAPI (`CryptProtectData` with `CRYPTPROTECT_UI_FORBIDDEN`) on Windows, and AES-256-GCM (`"GST\x02"` header) backed by a `0600` local key file on Linux and macOS.
