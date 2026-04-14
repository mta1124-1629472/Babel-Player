2026-04-12 - PythonJsonWorkerPool stderr read hangs
Learning: In `PythonJsonWorkerPool.cs`, when a worker fails to return a response (e.g. process exits unexpectedly or stops responding), reading from standard error using `ReadToEndAsync` can hang indefinitely if the process has not actually exited or hasn't closed its stderr stream. This masks errors and blocks the worker pool.
Action: Wait for the process to exit using `WaitForExitAsync` with a timeout, or use a limited read like `ReadLineAsync` (or just kill the process before reading) to avoid hanging. Since we are in the failure path, the process is likely unrecoverable.

2026-04-13 - Python subprocess read buffer limit and OS cancellation bug
Learning: Even with `Process.Kill` and a cancellation token, `StreamReader.ReadToEndAsync` on a redirected standard error pipe may not reliably honor cancellation in all .NET versions. If a dying python process dumps massive trackbacks (e.g., 50MB of stack frames), `ReadToEndAsync` allocates the entire trace and hangs waiting on a pipe that child handles keep open.
Action: Replace `ReadToEndAsync` in failure paths with a bounded line-by-line reader (e.g. `ReadLineAsync`) using a maximum lines/bytes cap, then silently drain the remainder. This ensures proper buffer flush on the OS side, tighter token granularity, and bounds the memory allocation for traceback diagnostics.
