# Audit UI Policy Leakage

Analyze a WinUI file and detect application workflow logic that should live in the App layer.

## Steps

1. Scan the provided WinUI file.
2. Identify fields or methods that decide application behavior instead of rendering UI.
3. Classify findings as:
   - presentation logic
   - workflow policy
4. For each workflow policy item:
   - explain why it belongs in the App layer
   - propose the correct App interface or service to own it.
5. Produce a refactor plan that removes policy logic from the UI.

## Output

- list of policy leaks
- recommended App-layer ownership
- safe refactor plan
