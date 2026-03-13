# Service Boundary Extraction

Identify code that should be moved behind an App service interface.

## Steps

1. Analyze a file or module.
2. Detect logic that coordinates multiple systems.
3. Determine if the logic belongs in an App workflow service.
4. Propose a service interface.
5. Provide refactored code examples.

## Output

- proposed interface
- moved methods
- updated calling code
