# Architecture Boundary Review

Review code changes for violations of the repository architecture.

## Steps

1. Analyze modified files.
2. Check dependency direction:
   - WinUI → App → Core
3. Identify forbidden dependencies in WinUI.
4. Detect workflow logic placed in UI code.
5. Suggest architecture-compliant corrections.

## Output

- architecture violations
- suggested fixes
- risk assessment# architecture-review.md
