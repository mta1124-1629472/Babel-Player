🧹 [code health improvement] Add logging to empty catch blocks

🎯 **What:** The `Dispose` method in `CompositeInferenceHostManager` previously swallowed exceptions silently with empty `catch` blocks. This PR updates the class to accept an `AppLog` dependency and log any disposal failures.
💡 **Why:** Swallowing exceptions without logging makes it extremely difficult to diagnose runtime issues, especially resource leaks or hangs during teardown. Adding logging improves the maintainability and debuggability of the application.
✅ **Verification:**
- Successfully built the application.
- Reviewed and verified `DependencyLocator.cs` correctly injects the new dependency.
- Ran tests relating to `AppLog` and verified no regressions.
✨ **Result:** Exceptions thrown during the disposal of managed or containerized inference host managers are now written as warnings to the application log.
