with open('Services/AppLog.cs', 'r') as f:
    content = f.read()

import re

# Conflict 1
content = re.sub(
    r'<<<<<<< HEAD\n\s*catch \(Exception ex\) \{ System.Diagnostics.Debug.WriteLine\(\$\"Failed to write log line: \{ex.Message\}\"\); \}\n=======\n\s*catch \(Exception ex\) \{ System.Diagnostics.Debug.WriteLine\(\$\"Failed to write log \'{LogFilePath}\': \{ex\}\"\); \}\n>>>>>>> origin/main\n',
    r'                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to write log \'{LogFilePath}\': {ex}"); }\n',
    content
)

# Conflict 2
content = re.sub(
    r'<<<<<<< HEAD\n\s*catch \(Exception ex\) \{ System.Diagnostics.Debug.WriteLine\(\$\"Failed to drain log line: \{ex.Message\}\"\); \}\n=======\n\s*catch \(Exception ex\) \{ System.Diagnostics.Debug.WriteLine\(\$\"Failed to drain log \'{LogFilePath}\': \{ex\}\"\); \}\n>>>>>>> origin/main\n',
    r'                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to drain log \'{LogFilePath}\': {ex}"); }\n',
    content
)

with open('Services/AppLog.cs', 'w') as f:
    f.write(content)
