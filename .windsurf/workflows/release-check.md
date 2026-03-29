---
auto_execution_mode: 2
description: Pre-flight checklist before shipping a build
---
You are performing a release pre-flight checklist before shipping or sharing a build.

## Steps
1. **Build verification**
   - Clean build from scratch
   - Verify build passes without warnings/errors
   - Confirm all artifacts are generated

2. **Test verification**
   - Run full test suite
   - Ensure all critical tests pass
   - Check test coverage if applicable

3. **Secrets scan**
   - Scan for hardcoded secrets, keys, or tokens
   - Check environment files and configs
   - Verify no sensitive data in logs or artifacts

4. **Version info**
   - Confirm version numbers are correct
   - Check changelog is updated
   - Verify build metadata

5. **Happy-path run**
   - Perform basic user workflow
   - Verify core functionality works
   - Check UI/CLI behavior

6. **Documentation check**
   - Verify README is up to date
   - Check API docs if applicable
   - Confirm installation instructions work

## Commands (copy/paste as needed)
```bash
# Clean build
dotnet clean && dotnet build --configuration Release
# or
rm -rf node_modules dist && npm ci && npm run build

# Full test suite
dotnet test --configuration Release
# or
npm test

# Secrets scan (basic patterns)
git grep -i "password\|secret\|key\|token\|api_key\|ghp_"
grep -r -i "password\|secret\|key\|token\|api_key\|ghp_" --include="*.js" --include="*.ts" --include="*.json" --include="*.cs" --include="*.yaml" --include="*.yml" .

# Check environment files
cat .env.example
cat .env.local 2>/dev/null || echo "No .env.local found"

# Version info
git describe --tags
git log --oneline -5
cat package.json | grep version
# or
cat *.csproj | grep Version

# Find artifacts
find . -name "*.exe" -o -name "*.dll" -o -name "*.tar.gz" -o -name "*.zip" -o -name "dist" -o -name "out"

# Documentation check
cat README.md | head -20
ls docs/ 2>/dev/null || echo "No docs/ directory"
```

## Secrets scan checklist
- [ ] No hardcoded passwords/tokens in source
- [ ] No API keys in config files
- [ ] Environment variables properly referenced
- [ ] No sensitive data in logs
- [ ] Test secrets don't match production

## Release checklist
- [ ] Build passes cleanly
- [ ] All tests pass
- [ ] Version numbers are correct
- [ ] Changelog is updated
- [ ] Documentation is current
- [ ] Happy path works
- [ ] No secrets leaked
- [ ] Artifacts are properly named
- [ ] Installation instructions tested

## What to capture
- Build output and artifacts list
- Test results summary
- Secrets scan results
- Version information
- Any issues found and their resolution

## Next steps
- [ ] Fix any issues found during checks
- [ ] Re-run verification after fixes
- [ ] Tag the release if applicable
- [ ] Create release notes
- [ ] Deploy/share artifacts
