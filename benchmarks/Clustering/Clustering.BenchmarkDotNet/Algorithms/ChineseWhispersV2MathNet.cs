using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Single;

namespace Clustering.BenchmarkDotNet.Algorithms
{
    public class ChineseWhispersV2MathNet : IClusteringAlgorithm
    {
        private readonly double _clusterDistance;
        private readonly List<Node> _nodes = new List<Node>();

        public ChineseWhispersV2MathNet(double clusterDistance)
        {
            _clusterDistance = clusterDistance;
        }

        public Guid GetCluster(float[] encoding)
        {
            var vector = Vector.Build.Dense(encoding);

            var clusterId = _nodes.Where(x => Distance(vector, x.Encoding) < _clusterDistance)
                .GroupBy(x => x.ClusterId)
                .OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;


            if (clusterId == null)
                clusterId = Guid.NewGuid();
            //Console.WriteLine($"Created new cluster {clusterId}");

            var newNode = new Node {ClusterId = clusterId.Value, Encoding = vector};
            _nodes.Add(newNode);

            return newNode.ClusterId;
        }

        public void OptimizeClusters()
        {
            throw new NotImplementedException();
        }

        public int ClusterCount => _nodes.Select(x => x.ClusterId).Distinct().Count();

        public int MaxClusterSize => _nodes.GroupBy(x => x.ClusterId).OrderByDescending(x => x.Count()).First().Count();

        private double Distance(Vector<float> left,
            Vector<float> right)
        {
            var distanceVector = left - right;
            var distance = Math.Sqrt(distanceVector.DotProduct(distanceVector));

            return distance;
        }

        private class Node
        {
            public Vector<float> Encoding { get; set; }

            public Guid ClusterId { get; set; }
        }
    }
}