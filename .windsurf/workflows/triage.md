---
auto_execution_mode: 3
description: Triage bugs and capture repro artifacts quickly
---
You are triaging a bug report to get to the smallest repro and identify likely owners fast.

## Steps
1. **Identify repro steps**
   - Extract exact steps to reproduce the issue
   - Note any prerequisites (data, config, environment)
   - Confirm the issue is reproducible

2. **Expected vs actual behavior**
   - Clearly state what should happen
   - Document what actually happens
   - Include error messages, stack traces, or visual artifacts

3. **Last-known-good commit (if applicable)**
   - Ask when the issue started
   - Use git bisect if needed to find the breaking change
   - Note any recent changes that could be related

4. **Locate logs and artifacts**
   - Identify where logs are written
   - Collect any crash dumps, screenshots, or output files
   - Note timestamps and file sizes

5. **Initial inspection checklist**
   - Check for obvious causes: missing files, permissions, config errors
   - Verify environment variables and runtime versions
   - Look for recent dependency changes

6. **Assign likely owners**
   - Based on the affected component/module
   - Consider recent commit authors
   - Note any cross-team dependencies

## Commands (copy/paste as needed)
```bash
# Git bisect (if issue is recent)
git bisect start
git bisect bad HEAD
git bisect good <last-known-good-commit>
git bisect run <repro-command>

# Find recent changes
git log --oneline -10
git diff HEAD~5..HEAD --name-only

# Check logs
tail -f logs/app.log
# or
journalctl -u <service>

# Environment check
printenv | grep -E "(NODE_ENV|DOTNET_ENV|PATH)"
dotnet --version
node --version
python --version

# Find artifacts
find . -name "*.log" -o -name "*.crash" -o -name "*.dump" -o -name "screenshot*" -newer /dev/null 2>/dev/null

# Check dependencies
npm ls
# or
dotnet list package
# or
cargo tree
```

## What to capture
- Exact repro steps
- Error messages with full stack traces
- Environment details (OS, runtime versions)
- Recent code changes
- Log file locations and timestamps
- Screenshots or visual artifacts if applicable
- Configuration files (redacted if needed)

## Triage template
```
### Repro Steps
1. 
2. 
3. 

### Expected vs Actual
- Expected: 
- Actual: 

### Last Known Good
- Commit: 
- Date: 

### Logs/Artifacts
- Location: 
- Timestamps: 

### Likely Owner(s)
- 
- 
```

## Next steps
- [ ] Confirm repro steps work
- [ ] Collect all artifacts
- [ ] Identify likely owner(s)
- [ ] Create issue with captured details
- [ ] Assign to appropriate team/person
