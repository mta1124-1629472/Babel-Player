---
auto_execution_mode: 2
description: Gather build context and debug info quickly
---
You are collecting build and debug context to diagnose build failures or unexpected behavior.

## Steps
1. **Print tool versions**
   - Show versions for the primary build system and any relevant runtimes
   - Note any version mismatches or missing tools

2. **Clean build output location**
   - Clear previous artifacts to ensure a fresh build
   - Verify build directory exists and is writable

3. **Run build with verbose output**
   - Execute the build with maximum verbosity
   - Capture all warnings and errors

4. **Collect errors/warnings**
   - Extract error messages and line numbers
   - Identify patterns (missing deps, type errors, etc.)

5. **Point to logs/artifacts**
   - Show where build logs and artifacts are located
   - Identify any generated files that may be relevant

6. **Check test output (if applicable)**
   - Run tests with verbose output
   - Capture failing test names and stack traces

## Commands (copy/paste as needed)
```bash
# Tool versions
dotnet --version
node --version
npm --version
rustc --version
cargo --version
cmake --version
make --version

# Clean
dotnet clean
# or
rm -rf node_modules dist
# or
cargo clean

# Verbose build
dotnet build --verbosity detailed
# or
npm run build -- --verbose
# or
cargo build --verbose

# Tests with output
dotnet test --verbosity detailed
# or
npm test -- --verbose
# or
cargo test -- --nocapture

# Find artifacts
find . -name "*.log" -o -name "*.out" -o -name "dist" -o -name "target" -o -name "bin"
```

## What to capture
- Build command used
- Full error messages with line numbers
- Tool versions
- Any dependency resolution failures
- Permission or path issues
- Test failures with stack traces

## Next steps checklist
- [ ] Identify the first error in the output
- [ ] Check for missing dependencies
- [ ] Verify environment variables
- [ ] Confirm all required tools are installed
- [ ] Check for permission issues on build directories
