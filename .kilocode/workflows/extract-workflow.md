# Extract Workflow From UI

Refactor a UI file by moving application workflow logic into the App layer.

## Steps

1. Identify workflow clusters in the file.
2. Separate logic into:
   - UI rendering
   - command forwarding
   - workflow policy
3. Move workflow policy into App services.
4. Replace UI logic with shell interface calls.
5. Ensure behavior remains unchanged.

## Constraints

- UI must not reference Core.
- UI must only call shell interfaces.
- Workflow logic must live in BabelPlayer.App.

## Output

- extracted workflow service
- updated UI file
- explanation of changes
