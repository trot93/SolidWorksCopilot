<img width="1918" height="1018" alt="image" src="https://github.com/user-attachments/assets/34fe3c12-5fa6-4619-b087-31b9fba9f32f" />

## Roadmap

### Phase 1 — MVP (in progress)
Turn natural language into SOLIDWORKS feature sequences inside a native add-in.

| Sprint | Description | Status |
|--------|-------------|--------|
| Sprint 1 | Conversational clarification — cheap pre-pass, targeted Q&A, deferred workspace scan | ✅ Done |
| Sprint 2 | Image attachment — attach a sketch or reference photo to ground generation | ✅ Done |
| Sprint 3 | Incremental step generation — review and correct step by step, not all at once | ⏳ Pending |

### Phase 2 — Intelligence upgrade
| Sprint | Description | Status |
|--------|-------------|--------|
| Sprint 4 | Critique pass — second LLM call acts as a senior engineer review | ⏳ Pending |
| Sprint 5 | Mode B error diagnosis — capture SW failures, diagnose, return ranked fixes | ⏳ Pending |
| Future | Domain-specific model — method-based JSON training: teaches the LLM *how to think about CAD*, not just what to output | 🔬 Research |

> **What's already done:** Sprint 1 introduced a cheap clarification pre-pass (Haiku, ~250 tokens) before any expensive generation call. The add-in asks 1–2 targeted questions, uses the LLM's own knowledge base for known standards (NEMA sizes, bolt specs, etc.), and only scans the workspace after clarification. This alone moves baseline accuracy from ~60% toward ~80%.
