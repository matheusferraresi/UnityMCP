# MCP Tool Optimization Analysis

**Date:** 2026-01-28
**Context:** After optimizing `uitoolkit_query` to use compact output with drill-down refs, this analysis identifies other tools that could benefit from similar patterns.

## Problem Statement

MCP tools that return large amounts of data waste AI context tokens and can hit response size limits. The `uitoolkit_query` fix demonstrated that:
- Default shallow depth (2) with drill-down refs works better than dumping everything
- Text search finds specific elements without loading full trees
- Compact output (short keys, omit empty fields) reduces token usage significantly

## Tools Analyzed

### HIGH Risk - Needs Optimization

#### 1. ReadConsole (`ReadConsole.cs`)
**Issue:** Stacktraces can be massive, iterates through ALL console entries
**Current limits:** Pagination (default 50, max 500), text filter, cursor-based
**Gaps:**
- No total cap on results
- Stacktraces returned in full by default
- Could return thousands of entries

**Recommended fixes:**
- Default to last 20 entries (most recent)
- Add `max_entries` hard cap (e.g., 100)
- Truncate stacktraces by default, add `full_stacktrace: true` option
- Add severity summary (X errors, Y warnings) in response

#### 2. ProfilerTools (`ProfilerTools.cs`)
**Issue:** Frame-by-frame profiler data grows with recording duration
**Current limits:** `includeFrameDetails` boolean flag
**Gaps:**
- No pagination for frame data
- Recording 60 seconds at 60fps = 3600 frames of data
- Delegates to `ProfilerJobManager.ToSerializable()` without size control

**Recommended fixes:**
- Default: Return summary stats only (avg fps, peak memory, hotspots)
- Add `frame_range: [start, end]` parameter for specific frames
- Paginate frame details (e.g., 50 frames per page)
- Add `top_n` parameter for top N slowest frames

#### 3. GetTestJob (`GetTestJob.cs`)
**Issue:** All test results returned at once
**Current limits:** `includeDetails` and `includeFailedTests` booleans
**Gaps:**
- No pagination for test results
- Projects with hundreds/thousands of tests = massive response
- No filtering by test name/category

**Recommended fixes:**
- Default: Return summary (total, passed, failed, duration)
- Add `test_filter` parameter for specific test name patterns
- Paginate test results (50 per page)
- Failed tests should be paginated separately

### MEDIUM Risk - Could Use Improvement

#### 4. ManageScene.GetHierarchy (`ManageScene.cs`)
**Issue:** Recursive hierarchy fetch with component lists per object
**Current limits:** `max_depth` (1-10), pagination (default 50, max 500)
**Gaps:**
- Default depth could still return large trees
- Components add overhead per object

**Recommended fixes:**
- Default depth 1 (immediate children only)
- Add ref_id drill-down pattern (like UIToolkit)
- Option to exclude component lists
- Compact output format

#### 5. ManageAsset.search (`ManageAsset.cs`)
**Issue:** Empty search pattern returns ALL project assets
**Current limits:** Pagination (default 50, max 500)
**Gaps:**
- `AssetDatabase.FindAssets("")` matches everything
- Large projects have thousands of assets

**Recommended fixes:**
- Require non-empty search pattern, OR
- Return error "Search pattern required" for empty searches
- Add `folder` parameter to scope searches
- Return count first, require confirmation for large result sets

#### 6. ManageScript.read (`ManageScript.cs`)
**Issue:** Returns entire file contents
**Current limits:** None
**Gaps:**
- Large scripts (1000+ lines) bloat context
- No way to read specific sections

**Recommended fixes:**
- Add `offset` and `limit` parameters (line-based)
- Default to first 100 lines
- Return `totalLines` in response for navigation
- Add `search` parameter to find specific code sections

#### 7. ManageGameObject (`ManageGameObject.cs`)
**Issue:** `GetAllSceneObjects()` loads entire scene into memory
**Current limits:** None for scene loading
**Gaps:**
- Large scenes with thousands of objects
- Called internally, could cascade

**Recommended fixes:**
- Add pagination to scene object listing
- Return count first, paginate on request
- Consider lazy loading pattern

### LOW Risk - Acceptable As-Is

- **FindGameObjects** - Already paginated (default 50, max 500), returns only instance IDs
- **ManageComponents** - Focused operations on single components
- **ExecuteMenuItem** - Simple action, minimal response
- **ManageMaterial/Shader/Texture** - Focused operations
- **ManagePrefabs** - Focused operations
- **SelectionTools** - Small result sets typically

## Implementation Pattern

Based on the successful `uitoolkit_query` refactor, the pattern for optimization is:

```
1. Default Response = Compact Summary
   - Counts, status, key metrics
   - No detailed data unless requested

2. Drill-Down via ID/Ref
   - First query returns refs/IDs
   - Second query expands specific items

3. Search/Filter First
   - Text search, name filter, type filter
   - Returns only matching items

4. Compact Output Format
   - Short keys (t, n, c instead of type, name, children)
   - Omit empty/null fields
   - Truncate long strings
   - Only include relevant metadata

5. Hard Limits
   - Max results cap (e.g., 100)
   - Max depth cap
   - Pagination with cursor
```

## Priority Order for Implementation

1. **ReadConsole** - Frequently used, stacktraces are massive
2. **GetTestJob** - Test suites can be huge
3. **ProfilerTools** - Profiler data explodes quickly
4. **ManageScene.GetHierarchy** - Common operation, recursive growth
5. **ManageAsset.search** - Edge case but can be bad
6. **ManageScript.read** - Usually fine, but large files exist

## Notes

- All changes should be backwards compatible (new parameters with smart defaults)
- Existing pagination mechanisms are good, just need tighter defaults
- Consider adding `compact: true` flag as universal option across tools
