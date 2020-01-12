using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Clustering.BenchmarkDotNet.Utils;

namespace Clustering.BenchmarkDotNet.Algorithms
{
    public class ChineseWhispersV4ClusterCenterDistance : IClusteringAlgorithm
    {
        private readonly double _clusterDistance;
        private static readonly float[] _max = Enumerable.Range(0, 128).Select(x => 1f).ToArray();
        private static readonly float[] _min = Enumerable.Range(0, 128).Select(x => -1f).ToArray();
        private readonly List<Node> _allSignificantNodes = new List<Node>();
        private readonly ReaderWriterLockSlim _clusterLock = new ReaderWriterLockSlim();
        private readonly List<Cluster> _clusters = new List<Cluster>();
        private readonly Dictionary<Guid, int> _clustersIndexes = new Dictionary<Guid, int>();
        private readonly ArrayPool<double> _doubleArrayPool = ArrayPool<double>.Create();
        private readonly ArrayPool<int> _intArrayPool = ArrayPool<int>.Create();
        private readonly ArrayPool<Node> _nodeArrayPool = ArrayPool<Node>.Create();
        private readonly ObjectPool<Node> _nodePool = new ObjectPool<Node>(() => new Node());
        private readonly ReaderWriterLockSlim _significantNodesLock = new ReaderWriterLockSlim();

        public ChineseWhispersV4ClusterCenterDistance(double clusterDistance)
        {
            _clusterDistance = clusterDistance;
        }

        public Guid GetCluster(float[] encoding)
        {
            var cluster = GetClusterInternal(encoding, _clusterDistance) ?? CreateCluster();

            var newNode = _nodePool.GetObject();
            newNode.ClusterId = cluster.ClusterId;
            newNode.Encoding = encoding;

            cluster.Lock.EnterReadLock();

            if (cluster.SignificantNodes.Count < 150)
                AddSignificantNode(cluster, newNode);

            cluster.AllNodes.Add(newNode);

            var needsOptimization = cluster.AllNodes.Count > 300;

            cluster.Lock.ExitReadLock();

            if (!needsOptimization)
                return newNode.ClusterId;

            OptimizeClustersSafe(cluster);

            return newNode.ClusterId;
        }

        public void OptimizeClusters()
        {
            //foreach (var pair in _clusters.Where(x => x.Value.SignificantNodes.Count < 3))
            //    _clusters.TryRemove(pair.Key, out _);

            foreach (var cluster in _clusters)
                OptimizeClustersSafe(cluster);
        }

        public int ClusterCount => _clusters.Count;

        public int MaxClusterSize =>
            _clusters.OrderByDescending(x => x.SignificantNodes.Count).First().SignificantNodes.Count;

        private Cluster CreateCluster()
        {
            var cluster = new Cluster
            {
                ClusterId = Guid.NewGuid(),
                SignificantNodes = new ConcurrentList<Node>(),
                AllNodes = new ConcurrentList<Node>()
            };

            _clusterLock.EnterWriteLock();

            _clusters.Add(cluster);
            _clustersIndexes.Add(cluster.ClusterId, _clusters.Count - 1);

            _clusterLock.ExitWriteLock();

            return cluster;
        }

        private void OptimizeClustersSafe(Cluster cluster)
        {
            cluster.Lock.EnterWriteLock();

            try
            {
                if (cluster.AllNodes.Count > 300)
                    OptimizeCluster(cluster);
            }
            finally
            {
                cluster.Lock.ExitWriteLock();
            }
        }

        private void AddSignificantNode(Cluster cluster, Node newNode)
        {
            cluster.SignificantNodes.Add(newNode);
            _significantNodesLock.EnterWriteLock();
            _allSignificantNodes.Add(newNode);
            _significantNodesLock.ExitWriteLock();
        }

        private Cluster GetClusterInternal(float[] encoding, double distance)
        {
            var clusterCount = _intArrayPool.Rent(_clusters.Count);
            var max = -1;
            var maxIndex = -1;

            _significantNodesLock.EnterReadLock();
            _clusterLock.EnterReadLock();

            for (var index = 0; index < _allSignificantNodes.Count; index++)
            {
                var node = _allSignificantNodes[index];

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

            _significantNodesLock.ExitReadLock();
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
                var diff = new Vector<float>(lhs.Slice(i, Vector<float>.Count)) -
                           new Vector<float>(rhs.Slice(i, Vector<float>.Count));
                result += Vector.Dot(diff, diff);

                if (result > distanceSquared)
                    return false;
            }

            var actualDistance = Math.Sqrt(result);

            return actualDistance < distance;
        }


        private void OptimizeCluster(Cluster cluster)
        {
            var nodeList = cluster.AllNodes;
            var distances = _doubleArrayPool.Rent(nodeList.Count);

            MinMax(nodeList, out var min, out var max);
            var center = Center(max, min);

            for (var i = 0; i < nodeList.Count; i++)
                distances[i] = Distance(center, nodeList[i].Encoding);

            var oldSignificantNodes = cluster.SignificantNodes;

            var nodesByDistance = _nodeArrayPool.Rent(nodeList.Count);
            var current = 0;

            foreach (var nodeIndex in Enumerable.Range(0, nodeList.Count).OrderByDescending(x => distances[x]))
            {
                nodesByDistance[current] = nodeList[nodeIndex];
                current++;
            }

            cluster.SignificantNodes = new ConcurrentList<Node>(nodesByDistance.Take(150));
            cluster.AllNodes = new ConcurrentList<Node>(cluster.SignificantNodes);

            _doubleArrayPool.Return(distances, true);

            _significantNodesLock.EnterWriteLock();

            foreach (var oldSignificantNode in oldSignificantNodes)
                _allSignificantNodes.Remove(oldSignificantNode);

            foreach (var node in cluster.SignificantNodes)
                _allSignificantNodes.Add(node);

            foreach (var removedNode in nodesByDistance.Skip(150).Take(nodeList.Count - 150))
                _nodePool.PutObject(removedNode);

            _significantNodesLock.ExitWriteLock();

            _nodeArrayPool.Return(nodesByDistance);
        }

        private static Span<float> Center(Span<float> max, Span<float> min)
        {
            var distance = VectorUtils.Subtract(max, min);
            var midway = VectorUtils.Multiply(distance, 0.5f);
            var center = VectorUtils.Add(min, midway);

            return center;
        }

        private static void MinMax(ConcurrentList<Node> nodeList, out Span<float> min, out Span<float> max)
        {
            min = new float[128];
            max = new float[128];
            _min.CopyTo(min);
            _max.CopyTo(max);

            foreach (var node in nodeList)
            {
                VectorUtils.Max(max, node.Encoding, ref max);
                VectorUtils.Min(min, node.Encoding, ref min);
            }
        }

        private static double Distance(Span<float> lhs, Span<float> rhs)
        {
            if (lhs.Length > rhs.Length || lhs.Length == 0)
                throw new ArgumentOutOfRangeException();

            if ((lhs.Length & (Vector<float>.Count - 1)) != 0)
                throw new ArgumentOutOfRangeException("Not a multiple of vector length");

            var result = 0f;

            for (var i = 0; i <= lhs.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var diff = new Vector<float>(lhs.Slice(i, Vector<float>.Count)) -
                           new Vector<float>(rhs.Slice(i, Vector<float>.Count));
                result += Vector.Dot(diff, diff);
            }

            var actualDistance = Math.Sqrt(result);

            return actualDistance;
        }

        private class Cluster
        {
            public ConcurrentList<Node> SignificantNodes { get; set; }

            public Guid ClusterId { get; set; }

            public ConcurrentList<Node> AllNodes { get; set; }

            public ReaderWriterLockSlim Lock { get; } = new ReaderWriterLockSlim();
        }

        private class Node
        {
            public float[] Encoding { get; set; }

            public Guid ClusterId { get; set; }
        }
    }
}