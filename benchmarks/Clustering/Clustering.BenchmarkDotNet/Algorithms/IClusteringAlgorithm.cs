using System;

namespace Clustering.BenchmarkDotNet.Algorithms
{
    public interface IClusteringAlgorithm
    {
        Guid GetCluster(float[] encoding, double distance);

        void OptimizeClusters();

        public int ClusterCount { get; }

        public int MaxClusterSize { get; }
    }
}