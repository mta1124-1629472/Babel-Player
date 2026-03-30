## Contributor Rules

### Project posture

Babel Deck is being rebuilt as a sequence of vertical slices around one core product chain:

`source media -> timed transcript -> translated/adapted dialogue -> spoken dubbed output -> in-context preview and refinement`

Contributors should protect that center of gravity.
This repo should not drift into a shell-first, framework-first, or abstraction-first project.

### Before you start

Read:

- `PLAN.md` for milestone order and gates
- `docs/architecture.md` for the current structural map and major boundaries
- `AGENTS.md` for repo behavior rules
- any milestone-specific docs relevant to the area you are touching

If your intended work does not clearly support the current milestone, it is probably out of scope.

### Naming conventions

Use names consistently by context:

- Product / branding: `Babel Deck`
- Repository name: `Babel-Deck`
- .NET namespaces and assembly-style identifiers: `BabelDeck` or `Babel.Deck`
- Filenames and folders: follow the local convention already used in that area rather than inventing a new one midstream

Do not mix branding names and code identifiers casually inside the same surface.

### Scope discipline

Contributors are expected to work one milestone at a time.

Do not:

- start downstream features early
- add optional enhancements before the current milestone is proven
- refactor broadly just because a cleaner architecture is possible
- add speculative extension points for future providers, runtimes, or workflows

A narrower real feature is preferred over a broader partial one.

### Truthful behavior only

Do not merge code that gives a false impression of readiness.

That includes:

- fake buttons or surfaces that look functional but are not
- silent fallback paths
- “coming soon” behavior disguised as completed implementation
- runtime or local-model claims that have not been verified

If something is incomplete, it should be obviously incomplete.

### Verification requirements

Before opening or merging a PR, contributors should verify changes at the appropriate level.

Minimum expectation:

- `dotnet build`
- `dotnet test`

If the change affects behavior that can be manually exercised, also run the relevant smoke path for the current milestone and note what was verified.

A change is not done because it compiles.
It is done when the milestone gate it touches is actually demonstrated.

### Smoke notes

For milestone work, include a short smoke note in the PR description or related notes.

A good smoke note says:

- what was tested
- on what kind of input
- what exact gate was verified
- anything still missing or fragile

### Smoke note conventions

Store milestone smoke notes under `docs/smoke/` using this naming pattern:

- `milestone-01-foundation.md`
- `milestone-02-headless-libmpv.md`

Conventions:
- lowercase
- hyphen-separated
- two-digit milestone number
- no root-level milestone files
- no separate `*_COMPLETE.md` file if the smoke note already records gate status

Allowed status values:
- `complete`
- `partial`
- `failed`

A smoke note must include:
- metadata
- gate summary
- verified items
- unverified items
- concrete evidence
- conclusion
- deferred items

If any gate item remains unverified, the smoke note must not say `complete`.

### Smoke note location

Store milestone smoke notes under `docs/smoke/` using milestone-based filenames.
Avoid root-level smoke-note files and avoid separate completion-note files unless there is a specific reason they add information not already present in the smoke note.

Example:

“Loaded sample media, generated transcript, persisted artifacts, restarted app, and confirmed transcript reopened correctly. This verifies the ingest/transcript persistence gate.”

### Refactors

Refactors are allowed only when they do at least one of these:

- unblock the current milestone
- reduce real complexity in code being actively changed
- remove a proven source of instability

Refactors are not justified by:

- aesthetic preference
- architectural purity
- future-proofing alone
- desire to align the whole repo to a new pattern mid-milestone

### UI work

Do not prioritize shell polish over core workflow progress.

UI additions should generally serve one of these purposes:

- make the current milestone usable
- expose truthful state
- improve debugging or inspection
- support transcript/translation/TTS/preview flow directly

Avoid prestige UI work before the product loop is real.

### New abstractions

Before adding a new service, coordinator, factory, interface, or subsystem, ask:

- is this needed right now for the current milestone?
- does it model a real current behavior?
- is there a smaller honest version that works?

If the abstraction mainly serves imagined future needs, do not add it yet.

### Python and inference environment hygiene

When touching Python-backed inference work:

- keep the desktop app and inference runtime separated by an explicit contract
- document Torch, CUDA, driver, WSL, and runtime assumptions when adding or changing them
- avoid baking WSL-only assumptions into the main app unless the current milestone explicitly requires them
- treat containers and NVIDIA-managed serving as optional deployment paths until the local workflow has been proven
- keep model downloads, runtime assets, and application source concerns separate
- **JSON field contracts:** Python/C# boundary field names are explicit serialization contracts — not implementation details. Do not rely on implicit .NET casing. When Python emits snake_case or camelCase, C# must match deliberately. Any change to cross-language JSON field names must be updated on both sides together.

### JSON artifact contracts

When Python writes JSON that C# reads, the field names form an explicit contract:

- Python: writes `translatedText`, `sourceLanguage`, `segments`
- C#: reads via `GetProperty("translatedText")` or typed DTOs with matching names

Changes to these field names must be made on both Python (producer) and C# (consumer) sides simultaneously.
Do not use implicit .NET PascalCase conventions at Python/C# boundaries.

### Historical preservation

Do not delete prior working code or experiments without preserving them somewhere recoverable.
If you are replacing something, archive the old path first.

The repo has already lost useful working history before. Do not repeat that.

### PR expectations

A good PR is:

- tightly scoped
- aligned to one milestone or one blocker
- honest about what it does and does not complete
- accompanied by build/test results
- accompanied by smoke results when relevant

A bad PR:

- mixes milestone work with opportunistic polish
- introduces large unrelated refactors
- adds fake scaffolding
- claims completion without verifying behavior

### When you find a blocker

If you hit a real blocker:

- document it clearly
- fix only what is needed to remove the blocker
- avoid expanding into adjacent cleanup unless it is necessary

Known infrastructure risks may deserve early attention, but they should not become an excuse for sideways expansion.

### Definition of good contribution

A good contribution moves the repo closer to this real user outcome:

load media -> get transcript -> get translated/adapted dialogue -> generate spoken output -> preview/refine in context -> save and resume later

If your change does not strengthen that path, it needs a strong reason to exist.