---
paths:
  - "**/*.cs"
---

# C# — Allocation, Control Flow, Security, Performance

Formatting, typing expectations, testing requirements and logging conventions are always loaded from `rules/coding-standards.md` and are **not** repeated here. This file carries the deeper C# material that only matters once you are actually writing code.

Rules tagged `[enforced: …]` are verified by `Doctorly.DedicatedServer.Architecture.Tests`. Everything else is review-only. That suite reflects over `Domain`, `Domain.External`, `Application`, `Infrastructure` and `WebAPI` only — satellite assemblies (`Infrastructure.HPM`, `Infrastructure.MMI`, `SmartTags`, `Statistics.*`, `Forms`) are **not** covered, so an `[enforced]` tag does not protect them.

## String Handling & Allocation

Zero unnecessary heap allocation, in order of preference:

1. `ReadOnlySpan<char>` / `Span<char>` — inspection or parsing that does not escape the stack.
2. `ReadOnlyMemory<char>` / `Memory<char>` — slices stored or passed across `async` boundaries.
3. String interpolation (`$"..."`) — only when a new heap string is the intended final output. Never in a log call.
4. `StringBuilder` — last resort, for loops that build a string of unknown length.

* No `+` concatenation in loops or across multiple statements.
* `string.Equals(a, b, StringComparison.Ordinal)` (or `OrdinalIgnoreCase`) — never `==` for externally-supplied strings.
* `string.IsNullOrEmpty` / `string.IsNullOrWhiteSpace` — never `== null || == ""`.

## Control Flow & Simplicity

* Max two levels of nesting — extract a method or use early returns beyond that.
* Guard clauses at the top; linear happy path below.
* Every `if` that can be false has an explicit `else`, a guard return, or a `Result.Fail`.
* Logic readable without comments — if a comment is needed to explain *what*, simplify the code.
* Small, focused, intention-revealing classes with one reason to change. No anemic domain models, fat handlers, god services, static state, or service locators.

## Security — OWASP Top 10

| # | Risk | Rule |
|---|---|---|
| A01 | Broken Access Control | `[Authorize]` on every command; every query filters by `practiceId` — see CLAUDE.md |
| A02 | Cryptographic Failures | No PHI/PII in logs or plaintext. Secrets in env vars / `appsettings.local.json`. TLS everywhere. |
| A03 | Injection | Dapper: parameterized queries only, no string-built SQL. EF: never `FromSqlRaw` with user input. |
| A04 | Insecure Design | Domain invariants in the Domain layer — not bypassable via direct repository calls. |
| A05 | Security Misconfiguration | No debug endpoints in prod. CORS locked. Stack traces never in error responses. |
| A06 | Outdated Components | Keep NuGet packages current. No packages with known CVEs. |
| A07 | Auth Failures | JWT on every non-anonymous endpoint via `RequireValidTokenPolicy`. Never roll custom auth. |
| A08 | Data Integrity | Verify checksums on external feeds. No untrusted deserialization without schema validation. |
| A09 | Logging Failures | Log auth failures and denials. Never suppress silently — log at `Error` before returning `InfrastructureError`. |
| A10 | SSRF | No outbound HTTP to user-supplied URLs. Allowlist all outbound targets. |

## Performance & CPU-Efficient Idioms

* Paginate large data sets — never over-fetch.
* `.Any()` over `.Count > 0` / `.Count() > 0` — short-circuits after the first element.
* `TryGetValue` over `ContainsKey` + indexer — one hash probe instead of two.
* `switch` expression over `if-else` chains — the compiler emits a jump table for dense value sets.
* `HashSet<T>` for repeated `.Contains()` — O(1) versus O(n) per call on `List<T>`.
* Pattern matching `is` / property patterns — a single type check, eliminating the double cast.
* Defer `.ToList()` / `.ToArray()` to the last moment — LINQ chains are lazy; early materialisation wastes an allocation.
* `?? []` for null-guarding `IEnumerable<T>` — the compiler emits a singleton for the empty case.
* `Span<T>` / `Memory<T>` over intermediary string or array allocations.
