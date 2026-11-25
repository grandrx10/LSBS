using Unity.Profiling;

namespace LSBS
{
    /// <summary>Names for the profiler.</summary>
    public static class Prof
    {
        public static readonly ProfilerMarker Command = new("LSBS.Command"), Maxims = new("LSBS.Maxims"), Tanks = new("LSBS.Tanks"), Jobs = new("LSBS.Jobs"), Second = new("LSBS.Second"),
            DrawMaxims = new("LSBS.DrawMaxims"), DrawTanks = new("LSBS.DrawTanks"), Bodies = new("LSBS.Bodies"), Soldiers = new("LSBS.Soldiers"), Fx = new("LSBS.Fx"), Player = new("LSBS.Player");
    }
}
