using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Clustering.BenchmarkDotNet.Algorithms
{
    public class ChineseWhispersV6LockFree : IClusteringAlgorithm
    {
        private int _clusterIndexCounter = 0;
        private readonly double _clusterDistance;
        private readonly CustomConcurrentBag<Node> _allNodes = new CustomConcurrentBag<Node>();
        private readonly ConcurrentDictionary<Guid, Cluster> _clusters = new ConcurrentDictionary<Guid, Cluster>();
        private readonly ArrayPool<int> _intArrayPool = ArrayPool<int>.Create();
        private readonly ArrayPool<Node> _nodeArrayPool = ArrayPool<Node>.Create();
        private readonly Func<Guid, Cluster> _createClusterFunc;

        public ChineseWhispersV6LockFree(double clusterDistance)
        {
            _clusterDistance = clusterDistance;
            _createClusterFunc = guid => new Cluster
            {
                Index = Interlocked.Increment(ref _clusterIndexCounter),
                ClusterId = guid,
                Nodes = new ConcurrentBag<Node>()
            };
        }

        public Guid GetCluster(float[] encoding)
        {
            Cluster cluster = GetClusterInternal(encoding, _clusterDistance);

            if (cluster.Nodes.Count < 100)
                AddNode(cluster, new Node { ClusterId = cluster.ClusterId, Encoding = encoding, ClusterIndex = cluster.Index });

            return cluster.ClusterId;
        }

        public int ClusterCount => _clusters.Count;

        public int MaxClusterSize =>
            _clusters.Values.OrderByDescending(x => x.Nodes.Count).First().Nodes.Count;

        private void AddNode(Cluster cluster, Node newNode)
        {
            cluster.Nodes.Add(newNode);
            _allNodes.Add(newNode);
        }

        private Cluster GetClusterInternal(float[] encoding, double distance)
        {
            int[] clusterCount = _intArrayPool.Rent(_clusterIndexCounter);
            int max = -1;
            Guid clusterGuid = Guid.Empty;

            int count = _allNodes.CopyTo(_nodeArrayPool, out Node[] nodes);

            for (int i = 0; i < count; i++)
            {
                Node node = nodes[i];
                if (IsDistanceSmallerThan(encoding, node.Encoding, distance))
                {
                    int newCount = Interlocked.Increment(ref clusterCount[node.ClusterIndex]);
                    if (newCount > max)
                    {
                        max = newCount;
                        clusterGuid = node.ClusterId;
                    }
                }
            }

            _nodeArrayPool.Return(nodes);

            if (clusterGuid == Guid.Empty)
                clusterGuid = Guid.NewGuid();

            _intArrayPool.Return(clusterCount, true);

            return _clusters.GetOrAdd(clusterGuid, _createClusterFunc);
        }

        private static bool IsDistanceSmallerThan(Span<float> lhs, Span<float> rhs,
            double distance)
        {
            if (lhs.Length > rhs.Length || lhs.Length == 0)
                throw new ArgumentOutOfRangeException();

            if ((lhs.Length & (Vector<float>.Count - 1)) != 0)
                throw new ArgumentOutOfRangeException("Not a multiple of vector length");

            float result = 0f;
            double distanceSquared = Math.Pow(distance, 2);

            for (int i = 0; i <= lhs.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                // Get the distance of the two vectors.
                Vector<float> diff = new Vector<float>(lhs.Slice(i, Vector<float>.Count)) -
                           new Vector<float>(rhs.Slice(i, Vector<float>.Count));
                result += Vector.Dot(diff, diff);

                // If the result is already greater then the requested distance we can exit early.
                if (result > distanceSquared)
                    return false;
            }

            double actualDistance = Math.Sqrt(result);
            return actualDistance < distance;
        }

        private class Cluster
        {
            public int Index { get; set; }

            public Guid ClusterId { get; set; }

            public ConcurrentBag<Node> Nodes { get; set; }
        }

        private class Node
        {
            public int ClusterIndex { get; set; }

            public float[] Encoding { get; set; }

            public Guid ClusterId { get; set; }
        }
    }
}