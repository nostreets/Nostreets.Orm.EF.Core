using Xunit;

// These tests share ONE real database and one table, and the ORM opens/disposes a context per
// operation. Left parallel, xUnit runs the DB-backed collection alongside the others and runs
// produce sporadic `SqlException: A transport-level error has occurred ... the I/O operation has
// been aborted` — a DIFFERENT test each time, every one of them green in isolation. That is the
// harness tearing connections down under itself, not a product failure, and the distinction matters:
// a flaky suite trains you to re-run instead of read, which is how a real red gets waved through.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
