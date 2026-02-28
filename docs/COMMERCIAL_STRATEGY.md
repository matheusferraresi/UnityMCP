# UnixxtyMCP Commercial Strategy

**Last Updated**: February 2026
**Purpose**: Research and planning for Unity Asset Store commercialization.

---

## Market Analysis

### Current Unixxty MCP Landscape

| Product | Price | Tools | External Runtime | Stars/Downloads |
|---------|-------|-------|-----------------|-----------------|
| CoplayDev/unity-mcp | Free | ~34 | Python + uv | 5,800+ stars |
| CoderGamester/mcp-unity | Free | ~30 | Node.js | ~500 stars |
| IvanMurzak/Unity-MCP | Free | ~49 | .NET binary | ~1,000 stars |
| Union MCP (Skywork) | Free | ~20 | Node.js | New |
| Hot Reload for Unity | **$50** | 0 (not MCP) | None | 10,000+ sales |
| **Our Fork (target)** | **$25-40** | **60+** | **None** | - |

### Target Audience

1. **Solo indie devs** using Claude Code / Cursor / Windsurf for game development
2. **Small studios** (2-10 people) wanting to accelerate prototyping
3. **Game jam participants** who need maximum speed
4. **Unity educators** teaching AI-assisted development

### Why People Would Pay

The free MCPs all suffer from:
1. **External runtime hassle** - Python/Node.js setup, PATH issues, version conflicts
2. **Connection instability** - Dozens of "disconnected" bug reports across all repos
3. **No hot reload** - Must exit play mode for every code change
4. **No visual feedback** - AI can't "see" the game
5. **Destructive editing** - Agents replace entire files

We solve ALL of these.

---

## Competitive Positioning

### Our Unique Value Proposition

> **"The only Unixxty MCP that runs entirely inside the editor, survives domain reloads, and lets AI hot-patch your game while it's running."**

### Feature Matrix for Marketing

| Feature | Free MCPs | Hot Reload ($50) | **UnixxtyMCP Pro** |
|---------|-----------|------------------|-----------------|
| 60+ MCP Tools | ❌ (20-34) | ❌ (0) | ✅ |
| Zero Setup | ❌ | ✅ | ✅ |
| No External Runtime | ❌ | ✅ | ✅ |
| Survives Recompilation | ❌/Partial | ✅ | ✅ |
| AI Hot Reload in Play Mode | ❌ | ✅ (standalone) | ✅ (MCP-integrated) |
| AI Vision (screenshots) | ❌/1 | ❌ | ✅ |
| Smart Diff Editing | ❌/Buggy | ❌ | ✅ |
| Compile Error Feedback | ❌ | ❌ | ✅ |
| Automated Play Testing | ❌ | ❌ | ✅ |
| Batch Operations | ❌/1 | ❌ | ✅ |
| Works with Claude Code | ✅ | N/A | ✅ |
| Works with Cursor | ✅ | N/A | ✅ |
| Works with Windsurf | ✅ | N/A | ✅ |

---

## Pricing Models

### Option A: Single Price ($30)
- Everything included
- Simple, no confusion
- Competes well against Hot Reload ($50)
- Lower barrier to entry

### Option B: Free + Pro ($40)
- **Free**: Core 52 tools + resources (open source fork, compete with others)
- **Pro**: +hot_patch, +vision_capture, +smart_edit, +compile_watch, +debug_play
- More complex but drives adoption through free tier

### Option C: Subscription ($5/month)
- Unity Asset Store supports subscriptions
- Ongoing revenue for ongoing updates
- Higher friction for users

### Recommendation: **Option A ($30)** or **Option B with $35 Pro**
- Option A is simpler and still cheaper than Hot Reload
- Option B drives community adoption but requires maintaining two builds

---

## Asset Store Listing

### Name Ideas
1. **"MCP Studio for Unity"** - Professional, clear
2. **"AI Forge - Unixxty MCP Bridge"** - Catchy
3. **"Unixxty MCP Pro"** - Direct
4. **"Claude Unity Bridge"** - Too specific to one AI
5. **"GameDev AI Tools"** - Too generic

**Recommendation**: **"MCP Studio"** or **"Unixxty MCP Pro"**

### Key Selling Points (Asset Store description)

1. **Zero Setup** - Install the package, connect your AI. No Python, no Node.js, no terminal commands.
2. **60+ Tools** - The most comprehensive Unixxty MCP available. Manage scenes, GameObjects, scripts, animations, materials, UI, builds, tests, and more.
3. **AI Hot Reload** - Edit code during Play Mode and see changes instantly. No domain reload, no restart.
4. **AI Vision** - Your AI assistant can SEE your game through screenshots. Debug visual issues with multimodal models.
5. **Smart Editing** - Diff-based script editing that validates before compiling. No more broken scripts.
6. **Bulletproof Connection** - Native C plugin survives Unity recompilation. No disconnects, ever.
7. **Works With Everything** - Claude Code, Cursor, Windsurf, GitHub Copilot, any MCP-compatible AI.

### Screenshots Needed
1. AI generating a complete game scene from text prompt
2. Hot patch applying during play mode (before/after split)
3. Vision capture showing AI analyzing a screenshot
4. Compilation error feedback in structured format
5. Tool registry showing 60+ tools
6. Zero-config setup (install → working in 30 seconds)

---

## Revenue Projections (Conservative)

### Assumptions
- Unity Asset Store takes 30% cut
- Average price: $30
- Market: ~100,000 Unity developers using AI tools (growing fast)

### Scenarios

| Scenario | Monthly Sales | Monthly Revenue | Annual Revenue |
|----------|--------------|-----------------|----------------|
| Conservative | 50 | $1,050 | $12,600 |
| Moderate | 200 | $4,200 | $50,400 |
| Optimistic | 500 | $10,500 | $126,000 |

**Comparison**: Hot Reload for Unity reportedly makes $100K+/year at $50/license.

### Growth Drivers
1. AI-assisted game development is exploding (2025-2026 trend)
2. Unixxty MCP is becoming standard workflow
3. Claude Code / Cursor adoption growing rapidly
4. No serious paid competitor in MCP space
5. Hot reload integration is unique

---

## Legal Considerations

### License
- Our fork is from Bluepuff71/UnityMCP which is MIT licensed
- Harmony is MIT licensed
- We CAN sell MIT-licensed code on Asset Store (just must include MIT notice)
- Our additions are our own code

### What We Must Include
- MIT license notice for Bluepuff71/UnityMCP original code
- MIT license notice for Harmony
- Our own license for added tools (can be proprietary)

### What We Should Consider
- Terms of service for Asset Store
- Support policy (forums, Discord, email?)
- Refund policy (Asset Store standard)
- Unity version support policy (Unity 6+? Unity 2022+?)

---

## Launch Plan

### Pre-Launch (2-3 weeks)
1. Implement Phase 1 features (smart_edit, compile_watch, hot_patch, vision, debug_play)
2. Create demo project showing all features
3. Record promotional video (2-3 min)
4. Write Asset Store description and prepare screenshots
5. Set up support channel (Discord or GitHub Discussions)

### Launch
1. Publish on Unity Asset Store
2. Post on Reddit (r/unity3d, r/gamedev, r/ClaudeAI)
3. Post on Twitter/X with demo video
4. Submit to Unity newsletter
5. Share in MCP community Discord channels

### Post-Launch
1. Respond to reviews and bug reports
2. Release Phase 2 features (manage_ugui, file_import)
3. Maintain compatibility with new Unity versions
4. Add features based on user feedback

---

## Community Building

### Open Source Strategy
- Keep core fork open source (52 tools) - drives adoption
- Pro features are closed source add-on
- Accept community contributions to core
- Build reputation as the "serious" Unixxty MCP

### Documentation
- Comprehensive tool reference with examples
- Video tutorials for common workflows
- AGENTS.md for AI interaction patterns
- Integration guides for each AI IDE

---

## Risk Factors

| Risk | Impact | Mitigation |
|------|--------|------------|
| Unity ships built-in MCP | Very High | Ship fast, build community, add value beyond basic MCP |
| CoplayDev adds hot reload | High | Our architecture advantage (no Python) persists |
| Hot Reload for Unity adds MCP | High | We'd still have more tools + zero-config |
| Free alternatives improve | Medium | Stay ahead with unique features + stability |
| AI tools become free/commoditized | Medium | Focus on quality, stability, support |
| Unity 7 breaks compatibility | Medium | Test on beta, release updates quickly |
