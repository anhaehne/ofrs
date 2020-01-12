using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Clustering.BenchmarkDotNet.Algorithms
{
    public class ChineseWhispersV3Vectorized : IClusteringAlgorithm
    {
        private readonly ConcurrentBag<Node> _nodes = new ConcurrentBag<Node>();

        public Guid GetCluster(float[] encoding, double distance)
        {
            var clusterId = _nodes.Where(x => IsDistanceSmallerThan(encoding, x.Encoding, distance))
                .GroupBy(x => x.ClusterId)
                .OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;


            if (clusterId == null)
            {
                clusterId = Guid.NewGuid();
                //Console.WriteLine($"Created new cluster {clusterId}");
            }

            var newNode = new Node {ClusterId = clusterId.Value, Encoding = encoding};
            _nodes.Add(newNode);

            return newNode.ClusterId;
        }

        public void OptimizeClusters()
        {
            
        }

        public int ClusterCount => _nodes.Select(x => x.ClusterId).Distinct().Count();

        public int MaxClusterSize => _nodes.GroupBy(x => x.ClusterId).OrderByDescending(x => x.Count()).First().Count();

        public ISet<Guid> RecalculateClusters()
        {
            return null;
        }

        private static bool IsDistanceSmallerThan(Span<float> lhs, Span<float> rhs, double distance)
        {
            if (lhs.Length > rhs.Length || lhs.Length == 0)
                throw new ArgumentOutOfRangeException();

            if ((lhs.Length & (Vector<float>.Count - 1)) != 0)
                throw new ArgumentOutOfRangeException("Not a multiple of vector length");

            var result = 0f;
            var distanceSquared = Math.Pow(distance, 2);

            for (var i = 0; i <= lhs.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var diff = new Vector<float>(lhs.Slice(i, Vector<float>.Count)) -
                           new Vector<float>(rhs.Slice(i, Vector<float>.Count));
                result += Vector.Dot(diff, diff);

                if (result > distanceSquared)
                    return false;
            }

            return Math.Sqrt(result) < distance;
        }

        private class Node
        {
            public float[] Encoding { get; set; }

            public Guid ClusterId { get; set; }
        }
    }
}