# Phase 4: semantic search

**Scope (approved 2026-11-26):** find memories by meaning, not only by shared words, with everything staying on
this machine. Provider: **Ollama with `nomic-embed-text`** (your choice of option C). Claude/Haiku can't do this:
Anthropic doesn't offer an embedding model (their docs point to Voyage AI, a cloud service).

## What was built

| Piece | Where | Notes |
| --- | --- | --- |
| Schema v3 | `Storage/Migrations.cs` | `observation_vectors`: one L2-normalized float32 vector per observation (per model), `ON DELETE CASCADE` |
| Ollama client | `Embedding/OllamaEmbeddingClient.cs` | `/api/embed`, batched; `keep_alive` 30 min (a cold model load took 26 s here) |
| Embedding service | `Embedding/EmbeddingService.cs` | warms the model at start and when a session starts; embeds anything without a vector for the current model (new observations, saved memories, all of them after a model change); backs off 1 min while Ollama is unreachable |
| Vector index | `Embedding/VectorIndex.cs` | all vectors in memory (~3 KB each), brute-force SIMD cosine; no sqlite-vec (still prerelease, native) |
| Hybrid search | `Embedding/HybridSearcher.cs` | FTS5 bm25 top 50 + cosine top 50 (≥ `minSimilarity`), merged by reciprocal-rank fusion; keyword-only fallback that says so |
| Keyword fixes | `Memory/MemoryStore.cs` | question words ("how", "do", "us"…) ignored; prefix matching only for words of 4+ letters (found by the tests: "us" matched "uses") |
| CLI / MCP | `engram search --keyword\|--semantic`, MCP `search` `mode`, `engram status` "search:" line | |
| Retrieval eval | `engram eval FILE [--k N]`, `docs/retrieval-eval.example.json` | recall@k and hit@k for keyword vs semantic vs hybrid, plus each mode's misses |

What gets embedded: title, narrative, facts and file names (not raw tool output), with nomic's
`search_document:` / `search_query:` prefixes.

### Settings (`~/.engram/settings.json`)

```jsonc
"search": {
  "semantic": true,                        // false = keyword only
  "embeddingUrl": "http://127.0.0.1:11434",
  "embeddingModel": "nomic-embed-text",    // change it and everything is re-embedded in the background
  "documentPrefix": "search_document: ",
  "queryPrefix": "search_query: ",
  "keepAlive": "30m",
  "queryTimeoutMs": 3000,                  // then keyword-only for that search
  "minSimilarity": 0.5                     // vector-only matches below this are dropped
}
```

### Deviations from the design plan

- **No sqlite-vec.** Brute-force cosine over an in-memory copy is a few milliseconds at this scale and avoids a
  prerelease native extension on two platforms. Revisit past ~100k observations.
- **Semantic search is on by default** (the plan had it off), because it degrades to keyword-only by itself.
- **WSL runs its own Ollama** (your choice), installed by you (it needs `sudo`).

## Measured (this machine, 2026-11-26, RTX 4080 SUPER, real Ollama)

| What | Result |
| --- | --- |
| Model | nomic-embed-text, 768 dims, ~335 MB in VRAM while loaded |
| Cold model load | 26 s (first call); the worker warms it at start and at each SessionStart |
| Warm query embedding | ~25 ms; `engram search` end to end ~85 ms including process start |
| Embedding 8 observations | 0.5 s (batched) |
| Mini eval, 8 questions, top 3 | keyword 88%, semantic 100%, hybrid 100% recall |

Known limit: a small model doesn't always connect abstract phrasing ("malicious websites") with specific facts
("web pages cannot call it"); that memory ranked 3rd. This is what the real eval measures.

## Phase 4 gate (your run)

1. Re-install (repo root, `pwsh`): `pwsh scripts/install-dev.ps1 -Wsl`
2. **WSL**: install Ollama and the model (needs your password):
   ```bash
   curl -fsSL https://ollama.com/install.sh | sh
   ollama pull nomic-embed-text
   ```
   Then `engram worker stop` in WSL so the worker restarts with Ollama available.
3. `engram status` on each side: the `search:` line should read `keyword + meaning (nomic-embed-text), N of N
   observations embedded`. Existing observations are embedded in the background within a minute.
4. Try `engram search <a question in your own words>` with and without `--keyword`.
5. **Retrieval eval**: copy `docs/retrieval-eval.example.json`, write ~30 real questions with the ids that answer
   them (`engram search`, `engram show`), and run `engram eval yourfile.json`. Send me the table.
6. Gate passes when hybrid recall is at least as good as keyword everywhere and clearly better on questions
   phrased differently from the memory.

### Things to watch

- `engram status` shows `embeddings unavailable: …` when Ollama is down; search still works (keyword-only).
- The first search after Ollama unloaded the model (30 idle minutes) answers keyword-only while it reloads; the
  next one is hybrid again.
