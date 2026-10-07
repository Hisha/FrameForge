using Xunit;

// Avalonia's compositor and headless session are process-global UI resources. Running pixel
// capture tests concurrently can move a brush between dispatcher threads and make otherwise
// deterministic rendering tests fail with VerifyAccess. Keep this assembly serial.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
