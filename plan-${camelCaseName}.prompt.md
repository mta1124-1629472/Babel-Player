## Plan: Contract Lock + First Work Package (Reconciled)

This is an implementation-ready, test-first contract pack. It incorporates the latest hard decisions to remove behavior drift.

## Glossary
- Run: One translation execution instance for one media item, transcript revision, model, and target language.
- Transcript revision: Monotonic version for current media; increment whenever ordered transcript segment set changes in any translation-invalidating way.
- Requested mode: Persisted user subtitle mode intent.
- Effective mode: Runtime presentation mode computed from contract truth table.
- Queue-item identity: Stable queue entry identity (instance identity), not path string identity.
- Open cycle: Media-open lifecycle from selection/load start until play, failure, replacement, or resolved resume decision.
- Active translation state: Translation state authoritative for rendering.
- Cached translation state: Retained translation data not render-authoritative until revalidated.

---

## 1) Translation execution contract + guard rails

### A. Contract
- Translation is run-based, not toggle-based.
- A translation run is one orchestrated pass for one tuple:
  - media item
  - transcript revision
  - selected translation model
  - target language
- Translation enabled means new translation work may be scheduled.
- Translation disabled means:
  - no new translation work may start
  - any in-flight run must be canceled
  - translated text is treated as unavailable for effective rendering
- Cached translations may be retained for fast re-enable, but are not authoritative active translation state while disabled.

### B. Source of truth
- Translation lifecycle truth: SubtitleApplicationService.
- Timed active translated segment truth: MediaSession.
- Render outcome truth: SubtitlePresentationProjector.
- Cached translations surviving disable/re-enable are non-authoritative until revalidated against (media, transcriptRevision, model, targetLanguage).

### B2. Invariants
- At most one active run exists per (mediaPath, transcriptRevision, modelKey, targetLanguage).
- Every translation write carries run token context.
- Writes from stale/canceled runs are ignored.
- Each source segment id maps to at most one active translation segment for current run context.
- Media change, transcript revision change, model change, or target language change invalidates prior run immediately.
- Translation disabled guarantees no translated lane is active for rendering regardless of cached data.
- Lifecycle status is emitted only from active run state:
  - preparing
  - translating
  - ready
  - canceled
  - failed

### C. Precedence / transition rules
- Precedence:
  1. media change
  2. transcript revision change
  3. translation disabled
  4. translation model or target language change
  5. explicit translation enable
  6. opportunistic active-cue translation
- On media change:
  - cancel active run
  - clear active run token
  - clear in-flight registry
  - invalidate active translation lane
- On transcript revision change:
  - cancel old run
  - increment revision
  - start new run only if translation enabled and model selected
- On translation disabled:
  - cancel active run
  - block new scheduling
  - render behaves as translation unavailable
- Opportunistic active-cue translation allowed only when:
  - no full-pass run is active
  - translation is enabled
  - model is selected
  - no active segment run for same segment is pending

### D. Non-goals
- Provider architecture redesign.
- Batching/perf optimization redesign.
- UI polish beyond status correctness.

### E. First failing tests to add
1. TranslationRun_StaleRunWritesAreIgnored
- Setup: media M, revision R1 run active, then revision increments to R2 and new run starts.
- Action: delayed R1 completion writes after R2 started.
- Expected: only R2 writes in active lane.

2. TranslationDisable_CancelsRun_ActiveLaneUnavailable
- Setup: enabled translation with in-flight run and cached translations.
- Action: disable translation.
- Expected: run canceled, no new writes, effective rendering treats translation unavailable.

3. TranslationRun_NoDuplicateSourceSegmentWrites
- Setup: same source segment receives concurrent write callbacks.
- Action: run callbacks in race.
- Expected: one active translation segment for that source id.

4. TranslationEnabledWithoutModel_NoRunStarts
- Setup: translation enabled, model absent.
- Action: trigger reprocess/start.
- Expected: no run starts, actionable status emitted.

### F. Smallest implementation sequence
1. Add run identity and transcript revision tracking in SubtitleApplicationService.
2. Add mandatory run token guard for all translation writes.
3. Centralize cancellation on media/revision/model/target-language/disable transitions.
4. Add active-versus-cached translation lane handling without broad refactor.
5. Ensure status emission is active-run scoped.

### G. Known uncertainties
- Cache placement choice:
  - same lane with strict render gate, or
  - separate cache structure
- Recommendation: separate cache from active lane if feasible, but do not block stabilization on that refactor.

### Completion notes
- Status: completed.
- Implemented: run-token lifecycle, stale-write guard, disable-time cancellation/invalidation, opportunistic suppression during active full run.
- Verified: stale run writes ignored, disable blocks delayed writes, enable without model does not start runs, concurrent per-cue same-segment callbacks result in single effective write.

---

## 2) Queue behavior contract

### A. Contract
- Queue has three disjoint domains:
  - NowPlaying
  - UpNext
  - History
- Reorder operates on UpNext only.
- Queue-item identity is stable entry identity, not path identity.

### B. Source of truth
- Queue transitions: PlaybackQueueController.
- Shell boundary invocation: ShellController.
- UI command forwarding only: Avalonia MainWindow panels.

### B2. Invariants
- NowPlaying never appears in UpNext simultaneously.
- MoveNext: UpNext head becomes NowPlaying; prior NowPlaying moves to History head.
- MovePrevious policy for this pack:
  - History head becomes NowPlaying
  - prior NowPlaying is inserted at UpNext head
- Reorder preserves UpNext cardinality and untouched-item relative order.
- ClearQueue empties UpNext only.

### C. Precedence / transition rules
- PlayNow:
  - remove first matching target identity/path from UpNext and History per contract
  - push prior NowPlaying to History
  - set NowPlaying
- MediaEnded:
  - UpNext non-empty => MoveNext behavior
  - UpNext empty => NowPlaying moved to History then cleared
- Reorder never triggers implicit playback.
- Path-only fallback must be documented first-match behavior if stable id not supplied.

### D. Non-goals
- Replacing queue/history model.
- New playlist architecture.
- Drag-drop UX redesign.

### E. First failing tests to add
1. QueueReorder_PreservesNowPlayingAndHistory
2. QueueReorder_DuplicatePaths_TargetsSingleIdentity
3. MediaEnded_TransitionsDeterministically
4. ClearQueue_DoesNotMutateNowPlayingOrHistory

Each test must include setup/action/expected aligned to contract rows.

### F. Smallest implementation sequence
1. Add explicit reorder API using stable identity/index in PlaybackQueueController.
2. Surface reorder command through ShellController.
3. Replace UI clear-and-rebuild reorder path.
4. Keep existing behavior unchanged outside reorder path.

### G. Known uncertainties
- Whether current UI can provide stable queue-item identity without adding a small id field.

### Completion notes
- Status: completed.
- Implemented: index-based reorder command surfaced through ShellController and consumed by Avalonia queue drag-drop path.
- Verified: reorder preserves NowPlaying/History boundaries and deterministic movement for duplicate-path queue entries.

---

## 3) Subtitle render-mode truth table

### A. Contract
- RequestedMode is persisted intent.
- EffectiveMode is computed presentation mode.
- EffectiveMode is a pure function of:
  - RequestedMode
  - translation enabled
  - source-only override
  - source text present
  - translation text present
  - source equals translation
- Mode semantics:
  - Off: no subtitle content
  - SourceOnly: source text only
  - TranslationOnly: translation text only
  - Dual: translation primary and source secondary when both present and meaningfully different

### B. Source of truth
- Effective presentation truth: SubtitlePresentationProjector.
- Intent transition and override lifetime: SubtitleWorkflowController.
- Persisted intent: ShellPreferencesService.
- Shell/UI consumes computed result only; no extra fallback logic.

### B2. Invariants
- Off always renders no subtitle text.
- TranslationOnly never falls back to source text.
- Dual never renders duplicate lines when normalized texts are equal.
- Source-only override is scoped to current media.
- Override expires on:
  - media change
  - translation disabled
  - explicit mode change clearing override
- Status text eligible only when:
  - mode is not Off
  - no subtitle text is renderable for effective mode

### C. Precedence / transition rules
- Precedence:
  1. RequestedMode Off
  2. source-only override for current media
  3. explicit truth-table mapping
- No hidden remapping outside truth table.

### D. Truth-table policy rows (locked)
1. Requested Off
- Effective: no text
- Status: hidden

2. Requested SourceOnly
- Source present: show source
- Source absent: no subtitle text, optional status
- Translation availability ignored

3. Requested TranslationOnly
- Translation enabled + translation present: show translation
- Translation enabled + translation absent: no subtitle text, optional loading/status
- Translation disabled: no subtitle text, optional unavailable status
- Never show source fallback

4. Requested Dual
- Source and translation present and different: show both
- Source and translation present and equal: show single-line result
- Only source present and translation enabled: degrade to source-only (policy locked)
- Only source present and translation disabled: treat dual as source-only (policy locked)
- Only translation present: show translation
- Neither present: no subtitle text, optional status

### E. Non-goals
- Visual redesign.
- New subtitle modes.
- Subtitle UX overhaul.

### F. First failing tests to add
1. SubtitleModeTruthTable_MatrixCoverage
- Setup: table-driven matrix across all rows.
- Action: compute effective presentation.
- Expected: exact effective mode/visibility/primary-secondary/status outcomes.

2. TranslationOnly_MissingTranslation_DoesNotShowSource
- Setup: requested TranslationOnly, source present, translation absent.
- Action: compute presentation.
- Expected: source not shown as fallback.

3. SourceOnlyOverride_IsMediaScoped
- Setup: override active for media A.
- Action: switch to media B.
- Expected: override not applied on media B.

### G. Smallest implementation sequence
1. Lock matrix tests first.
2. Implement projector mapping to satisfy matrix exactly.
3. Align controller override lifetime transitions.
4. Keep shell wiring unchanged.

### H. Known uncertainties
- Remaining acceptable uncertainty is wording only, not behavior.

### Completion notes
- Status: completed.
- Implemented: projector truth-table mapping with strict TranslationOnly no-source-fallback and Dual degradation policy.
- Verified: render-mode behavior locked by tests, including media-scoped source-only override behavior.

---

## 4) Resume/open/autoplay transition contract

### A. Contract
- Open cycle begins at media select/load start and ends when:
  - playback begins normally
  - resume decision is completed
  - media open fails
  - another media item replaces current cycle
- Resume decision is path-scoped and open-cycle-scoped.
- Manual opens and autoplay transitions have distinct prompt policy.

### B. Source of truth
- Resume decision truth: ShellController.
- Prompt UI only: MainWindow.
- Resume persistence/eligibility data truth: ResumePlaybackService.

### B2. Invariants
- At most one pending resume decision in current open cycle.
- Prompt applies only to currently opening media path.
- Resume seek occurs at most once per open cycle.
- Opening another media item clears prior cycle pending state.
- Autoplay never reuses prior media pending prompt state.

### C. Precedence / transition rules
- Precedence:
  1. explicit user action
  2. resume eligibility decision
  3. autoplay default behavior
  4. start from zero
- Manual open:
  - resume disabled => start normally
  - ineligible entry => start normally
  - eligible entry => pause once, prompt once
- Autoplay open:
  - never prompt
  - apply silent resume only if meaningful
  - else start from zero
- Explicit Resume:
  - seek saved position
  - clear pending decision
  - play
- Explicit Start Over:
  - clear saved entry for media
  - seek zero
  - clear pending decision
  - play

### D. Resume eligibility thresholds (locked)
- Meaningful resume requires all:
  - position >= 60 seconds
  - position >= 5% of duration
  - position not within final 3% of duration
  - duration above app minimum threshold, or 10 minutes if app threshold unavailable
- Same threshold applies to prompt eligibility and silent autoplay resume for this phase.

### E. Prompt policy (locked)
- Manual open:
  - eligible => prompt
- Autoplay:
  - eligible => silent resume
  - never prompt
- Explicit Play Now from queue:
  - treat as manual open
- Explicit Previous/History navigation:
  - treat as manual open

### F. Non-goals
- Resume persistence redesign.
- New prompt UX flows.
- Session-history redesign.

### G. First failing tests to add
1. ResumeManualOpen_Eligible_ShowsSinglePrompt
- Setup: eligible entry, manual-open context.
- Action: open handling.
- Expected: one pending decision, one pause.

2. ResumeAutoplay_NoPrompt_AppliesPolicy
- Setup: eligible entry, autoplay context.
- Action: open handling.
- Expected: no prompt, silent resume-or-zero per threshold.

3. ResumeDecision_AppliesSeekExactlyOnce
- Setup: pending decision exists.
- Action: apply Resume.
- Expected: one seek, one play, pending cleared.

4. OpenCycleChange_ClearsPendingDecision
- Setup: pending decision for media A.
- Action: open media B.
- Expected: pending for A cleared.

### H. Smallest implementation sequence
1. Introduce explicit open-cycle resume state in ShellController.
2. Add open-context input (manual/autoplay).
3. Centralize eligibility + precedence in ShellController.
4. Keep MainWindow as prompt view forwarding decisions only.
5. Centralize clear-on-end/fail/replace hooks.

### I. Known uncertainties
- Whether product wants different thresholds for silent autoplay versus prompted resume in a later phase.
- Recommendation for this phase: keep one shared threshold set.

### Completion notes
- Status: completed.
- Implemented: trigger-aware open context (manual/autoplay), load-scoped resume decisioning, no autoplay prompt, and centralized shell status text.
- Verified: precedence tests for load policy, pending-context mismatch clearing regression test, and shell wiring updates in WinUI/Avalonia.

---

## Cross-contract execution order
1. Add failing tests for all four contracts first.
2. Implement minimum guard/state/command changes to make tests pass.
3. Run focused App tests and targeted Avalonia smoke checks.

### Completion notes
- Status: completed.
- Sequence executed: contract-first test additions followed by minimal implementation slices and focused validation loops.

## Global non-goals
- No broad class decomposition.
- No backend unification project.
- No architectural rewrite.
- No UI redesign except behavior correctness changes.

### Completion notes
- Status: upheld.
- Verified: no architecture rewrite or broad decomposition; changes remained behavior-correctness-focused within existing seams.


## Progress update (2026-03-13)

Completed since this plan was drafted:
- Contract 1 (translation execution guard rails): implemented run token lifecycle, stale-write suppression, cancel-on-disable behavior, and added tests for stale run writes, disable-time write blocking, enable-without-model no-run, and opportunistic per-cue suppression while full run is active.
- Contract 2 (queue behavior): implemented app-owned index-based reorder path via QueueCommands/ReorderQueueItem and replaced shell clear+rebuild reorder flow.
- Contract 3 (subtitle render truth table): projector behavior locked to strict TranslationOnly no-source-fallback and Dual fallback policy; tests updated/added.
- Contract 4 (resume/open/autoplay): implemented open-trigger context (Manual/Autoplay), load-scoped resume policy precedence, autoplay no-prompt behavior, and trigger-aware open/resume status messages consumed by both WinUI and Avalonia.
- Validation: BabelPlayer.App.Tests currently passing (146 passed).

Remaining work to continue from here (next slice):
1. Resume/open/autoplay hardening for pending open-context lifetime edge cases: completed.
- stale pending context on MediaOpened path mismatch now clears immediately in ShellController
- regression coverage added for mismatch event followed by corrected open path
2. Translation contract: optional final hardening test for source-segment single-write idempotence under explicit callback races: completed.
- added concurrent per-cue callback regression proving single source-segment write outcome
3. Optional polish: align or retire legacy resume prompt controls in Avalonia view now that shell owns resume decisioning: completed.
- removed ResumePromptBorder block and obsolete resume prompt handlers/field wiring in Avalonia MainWindow view/code-behind


## Execution complete (2026-03-13)
- All scoped contract slices in this plan are implemented and validated.
- Latest validation baseline: dotnet test tests/BabelPlayer.App.Tests/BabelPlayer.App.Tests.csproj => 146 passed, 0 failed.
