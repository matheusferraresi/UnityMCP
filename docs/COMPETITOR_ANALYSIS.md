# Unixxty MCP Competitor Analysis

**Last Updated**: February 2026
**Purpose**: Comprehensive comparison of all Unixxty MCP implementations to inform our roadmap.

---

## The Landscape (Feb 2026)

| MCP Server | Claimed Tools | Actual Tool Endpoints | Language | External Runtime | Stars | License |
|------------|--------------|----------------------|----------|-----------------|-------|---------|
| **Our Fork (Bluepuff71)** | 52 | **52** | C# (Unity-native) | **None** | - | MIT |
| CoplayDev/unity-mcp | "86 capabilities" | **~34** | Python (uv) | Python + uv | 5,800+ | MIT |
| CoderGamester/mcp-unity | "33 tools" | **~30** | Node.js + C# | Node.js | - | MIT |
| IvanMurzak/Unity-MCP | ~49 tools | ~49 | C# (.NET exe) | .NET binary | - | MIT |
| Union MCP (Skywork) | ~20 | ~20 | Node.js | Node.js | - | Apache 2.0 |

### Why CoplayDev Claims "86"

They count each **action within a tool** as a separate "capability":
- `manage_gameobject` → create, modify, delete, duplicate, move_relative = **5 "capabilities"**
- `manage_animation` → 8+ actions = **8 "capabilities"**
- `manage_vfx` → particles (play/pause/stop/restart/get/set) + lines + trails = **15+ "capabilities"**
- `manage_scene` → create, load, save, hierarchy, etc. = **5+ "capabilities"**

So ~34 tools × ~2.5 avg actions ≈ 85 "capabilities."

By this same counting method, our 52 tools would be **120+ capabilities**.

---

## Tool-by-Tool Comparison

### What We Have That Others Don't

| Tool | Us | CoplayDev | CoderGamester | Union | IvanMurzak |
|------|-----|-----------|---------------|-------|------------|
| `package_manage` (5 actions) | ✅ | ❌ | `add_package` only | ❌ | ❌ |
| `validate_script_advanced` (Roslyn) | ✅ | Basic only | ❌ | Basic | ❌ |
| `console_write` | ✅ | ❌ | `send_console_log` | ❌ | ❌ |
| `recompile_scripts` | ✅ | ❌ | ✅ | ❌ | ❌ |
| UIToolkit suite (6 tools) | ✅ | `manage_ui` (1) | ❌ | ❌ | ❌ |
| Profiler (3 tools) | ✅ | ❌ | ❌ | ❌ | ❌ |
| Build system (2 tools) | ✅ | ❌ | ❌ | ❌ | ❌ |
| Selection (2 tools) | ✅ | In `manage_editor` | `select_gameobject` | ✅ | ✅ |
| `scene_screenshot` | ✅ | ❌ | ❌ | ✅ | ❌ |
| `search_tools` | ✅ | ❌ | ❌ | ❌ | ❌ |
| `batch_execute` (atomic undo) | ✅ | ✅ (buggy) | ✅ | ❌ | ❌ |
| `animation_controller` + `animation_clip` | ✅ | `manage_animation` | ❌ | ❌ | ❌ |

### What Others Have That We Don't (Yet)

| Feature | Who Has It | Priority |
|---------|-----------|----------|
| `manage_ui` (UGUI/Canvas runtime UI) | CoplayDev | HIGH |
| Vision/screenshots as base64 to AI | Union | HIGH |
| `apply_text_edits` (diff-based editing) | CoplayDev (buggy) | HIGH |
| `execute_custom_tool` (user-defined tools) | CoplayDev | MEDIUM |
| VFX Graph control (5 files) | CoplayDev | LOW |
| `debug_request_context` | CoplayDev | LOW |
| `manage_script_capabilities` | CoplayDev | LOW |
| Type reflection / schema | IvanMurzak, Union | MEDIUM |
| File import from disk | Union | MEDIUM |
| Custom MCP instructions/prompts | MCP spec, CoplayDev requested | MEDIUM |

---

## Architecture Comparison

### Connection Architecture

| Feature | Us (Bluepuff71) | CoplayDev | CoderGamester | Union | IvanMurzak |
|---------|-----------------|-----------|---------------|-------|------------|
| Transport | HTTP (native C proxy) | stdio (Python) | WebSocket | HTTP (Node.js) | HTTP (.NET exe) |
| Survives Domain Reload | ✅ Native plugin | Partial (reconnect) | ❌ (crashes) | ❌ | ❌ |
| External Process | **None** | Python + uv | Node.js | Node.js | .NET binary |
| Zero Config | ✅ | ❌ (setup required) | ❌ (npm build) | ❌ | ❌ |
| TLS Support | ✅ (optional) | ❌ | ❌ | ❌ | ❌ |
| API Key Auth | ✅ | ❌ | ❌ | ❌ | ❌ |

### Why Zero External Runtime Matters

1. **No "MCP server crashed/disconnected" issues** - The #1 bug category across CoplayDev (dozens of issues) and CoderGamester
2. **No Python/Node.js version conflicts** - CoplayDev has issues with pyenv, WSL2, macOS paths
3. **No npm/pip dependency hell** - CoderGamester has issues with missing node_modules
4. **Instant setup** - Install Unity package, done. No terminal commands needed

---

## Community Pain Points (from 1000+ GitHub Issues)

### Top Bug Categories

| Category | CoplayDev Issues | CoderGamester Issues | Our Exposure |
|----------|-----------------|---------------------|-------------|
| Connection drops / disconnects | 20+ issues | 15+ issues | **Zero** (native proxy) |
| Python/Node.js path issues | 10+ issues | 5+ issues | **Zero** (no external runtime) |
| Domain reload breaks server | 5+ issues | 8+ issues | **Zero** (native plugin survives) |
| Script editing corrupts code | 5+ issues | 2+ issues | Need `smart_edit` |
| Compilation error feedback | 3+ issues | - | Need `compile_and_watch` |
| IDE config confusion | 10+ issues | 10+ issues | Need better docs |

### Most Requested Features (Across All MCPs)

1. **Better script editing** - Don't replace entire files, use diffs (CoplayDev #multiple)
2. **Compilation feedback** - Know when/why compilation fails (CoplayDev #multiple)
3. **Custom prompts/instructions** - Per-project AI behavior (CoplayDev #828)
4. **Console clear** - Isolate errors from specific actions (IvanMurzak open issue)
5. **Type reflection** - What fields/methods does a component have? (IvanMurzak, Union)
6. **Runtime UI management** - Canvas, Buttons, Text (CoplayDev multiple)
7. **Vision/screenshots** - AI needs to "see" the game (Union differentiator)
8. **Play mode testing** - Automated verify after changes (CoplayDev roadmap)

---

## CoplayDev Roadmap (from their Wiki)

### Active Development
- Documentation improvements
- Per-call instance routing
- Code Coverage dependency resolution

### Mid-Term (3-6 months)
- Runtime MCP operation (AI controls running game)
- GenAI plugins for 2D/3D asset generation
- Script editing consolidation

### Long-Term (6-9+ months)
- Dependency injection framework
- "LLMs to interact with user-created games" (play mode)
- Runtime AI integration

### Backlog
- Visual scripting (Bolt/PlayMaker)
- Test coverage expansion
- Docker deployment
- Tool search/filtering

---

## Union MCP (Commercial Competitor)

**License**: Apache 2.0 (free, open source despite commercial positioning)
**Architecture**: Node.js

### Unique Features
- **Multimodal vision** - AI can "see" editor via screenshots
- **Type information retrieval** - Full C# type reflection
- **File import from filesystem** - Bring external assets in
- **Uses Unity's compiler for validation** - Not just linting

### What They Lack vs Us
- Only ~20 tools (we have 52)
- Requires Node.js external runtime
- No domain reload survival
- No batch operations
- No profiler/build tools
- No UIToolkit support
- No animation tools

---

## Key Takeaways

1. **We already have the most tools** - 52 vs 34 (CoplayDev) vs 30 (CoderGamester) vs 20 (Union)
2. **Our architecture is the best** - Zero external runtime, survives domain reload
3. **The "86 tools" claim is marketing** - Inflated counting of actions within tools
4. **Connection stability is our biggest advantage** - Competitors' #1 bug category doesn't affect us
5. **Gaps to fill**: Smart editing, compilation feedback, vision, hot reload, UGUI, type reflection
6. **Commercial opportunity**: No Unixxty MCP combines our stability + tool count + the planned killer features
