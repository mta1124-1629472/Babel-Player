#!/bin/bash
awk '
/^<<<<<<< HEAD/ { in_conflict = 1; print "        catch (Exception ex)"; next }
/^=======/ { if (in_conflict) next }
/^>>>>>>> / { if (in_conflict) { in_conflict = 0; print "        {\n            _log.Warning($\"Failed to check if managed GPU host script changed: {ex.Message}\");"; next } }
{ if (!in_conflict) print }
' Services/ManagedVenvHostManager.cs > temp.cs
mv temp.cs Services/ManagedVenvHostManager.cs
