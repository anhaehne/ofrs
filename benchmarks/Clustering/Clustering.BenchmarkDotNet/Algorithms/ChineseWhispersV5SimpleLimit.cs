using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Clustering.BenchmarkDotNet.Algorithms
{
    public class ChineseWhispersV5SimpleLimit : IClusteringAlgorithm
    {
        private readonly double _clusterDistance;
        private readonly List<Node> _allNodes = new List<Node>();
        private readonly ReaderWriterLockSlim _allNodesLock = new ReaderWriterLockSlim();
        private readonly ReaderWriterLockSlim _clusterLock = new ReaderWriterLockSlim();
        private readonly List<Cluster> _clusters = new List<Cluster>();
        private readonly Dictionary<Guid, int> _clustersIndexes = new Dictionary<Guid, int>();
        private readonly ArrayPool<int> _intArrayPool = ArrayPool<int>.Create();

        public ChineseWhispersV5SimpleLimit(double clusterDistance)
        {
            _clusterDistance = clusterDistance;
        }

        public Guid GetCluster(float[] encoding)
        {
            var cluster = GetClusterInternal(encoding, _clusterDistance) ?? CreateCluster();

            if (cluster.Nodes.Count < 100)
                AddNode(cluster, new Node {ClusterId = cluster.ClusterId, Encoding = encoding});

            return cluster.ClusterId;
        }

        public void OptimizeClusters()
        {
        }

        public int ClusterCount => _clusters.Count;

        public int MaxClusterSize =>
            _clusters.OrderByDescending(x => x.Nodes.Count).First().Nodes.Count;

        private Cluster CreateCluster()
        {
            var cluster = new Cluster
            {
                ClusterId = Guid.NewGuid(),
                Nodes = new ConcurrentList<Node>()
            };

            _clusterLock.EnterWriteLock();

            _clusters.Add(cluster);
            _clustersIndexes.Add(cluster.ClusterId, _clusters.Count - 1);

            _clusterLock.ExitWriteLock();

            return cluster;
        }

        private void AddNode(Cluster cluster, Node newNode)
        {
            cluster.Nodes.Add(newNode);
            _allNodesLock.EnterWriteLock();
            _allNodes.Add(newNode);
            _allNodesLock.ExitWriteLock();
        }

        private Cluster GetClusterInternal(float[] encoding, double distance)
        {
            var clusterCount = _intArrayPool.Rent(_clusters.Count);
            var max = -1;
            var maxIndex = -1;

            _allNodesLock.EnterReadLock();
            _clusterLock.EnterReadLock();

            for (var index = 0; index < _allNodes.Count; index++)
            {
                var node = _allNodes[index];

                if (IsDistanceSmallerThan(encoding, node.Encoding, distance))
                {
                    var clusterIndex = _clustersIndexes[node.ClusterId];
                    var newCount = Interlocked.Increment(ref clusterCount[clusterIndex]);

                    if (newCount > max)
                    {
                        max = newCount;
                        maxIndex = clusterIndex;
                    }
                }
            }

            var cluster = maxIndex != -1 ? _clusters[maxIndex] : null;

            _allNodesLock.ExitReadLock();
            _clusterLock.ExitReadLock();

            _intArrayPool.Return(clusterCount, true);

            return cluster;
        }

        private static bool IsDistanceSmallerThan(Span<float> lhs, Span<float> rhs,
            double distance)
        {
            if (lhs.Length > rhs.Length || lhs.Length == 0)
                throw new ArgumentOutOfRangeException();

            if ((lhs.Length & (Vector<float>.Count - 1)) != 0)
                throw new ArgumentOutOfRangeException("Not a multiple of vector length");

            var result = 0f;
            var distanceSquared = Math.Pow(distance, 2);

            for (var i = 0; i <= lhs.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                // Get the distance of the two vectors.
                var diff = new Vector<float>(lhs.Slice(i, Vector<float>.Count)) - 
                           new Vector<float>(rhs.Slice(i, Vector<float>.Count));
                result += Vector.Dot(diff, diff);

                // If the result is already greater then the requested distance we can exit early.
                if (result > distanceSquared)
                    return false;
            }

            var actualDistance = Math.Sqrt(result);
            return actualDistance < distance;
        }

        private class Cluster
        {
            public Guid ClusterId { get; set; }

            public ConcurrentList<Node> Nodes { get; set; }

            public ReaderWriterLockSlim Lock { get; } = new ReaderWriterLockSlim();
        }

        private class Node
        {
            public float[] Encoding { get; set; }

            public Guid ClusterId { get; set; }
        }
    }
}