using Xunit;

namespace CordMic.Tests;

/// <summary>Timing gates (PLC-7, RS-5, NR-10/CPU-3, CPU-4) run alone so other test classes do not share the CPU.</summary>
[CollectionDefinition("cpu-timing", DisableParallelization = true)]
public class CpuTimingCollection { }
