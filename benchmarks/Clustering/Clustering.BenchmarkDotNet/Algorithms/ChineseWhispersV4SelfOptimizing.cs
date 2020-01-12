using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Clustering.BenchmarkDotNet.Algorithms
{
    public class ChineseWhispersV4SelfOptimizing : IClusteringAlgorithm
    {
        private static readonly float[] _max = Enumerable.Range(0, 128).Select(x => 1f).ToArray();
        private static readonly float[] _min = Enumerable.Range(0, 128).Select(x => -1f).ToArray();
        private readonly List<Node> _allSignificantNodes = new List<Node>(10000);
        private readonly ConcurrentDictionary<Guid, Cluster> _clusters = new ConcurrentDictionary<Guid, Cluster>();
        private readonly ReaderWriterLockSlim _significantNodesLock = new ReaderWriterLockSlim();
        private readonly ObjectPool<List<Guid>> _guidLists = new ObjectPool<List<Guid>>(() => new List<Guid>());

        public Guid GetCluster(float[] encoding, double distance)
        {
            var clusterId = GetClusterId(encoding, distance);

            var newNode = new Node {ClusterId = clusterId ?? Guid.NewGuid(), Encoding = encoding};

            var cluster = GetOrAddCluster(newNode.ClusterId);

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
            foreach (var pair in _clusters.Where(x => x.Value.SignificantNodes.Count < 3))
                _clusters.TryRemove(pair.Key, out _);

            foreach (var cluster in _clusters.Values)
                OptimizeCluster(cluster);
        }

        public int ClusterCount => _clusters.Count;

        public int MaxClusterSize => _clusters.OrderByDescending(x => x.Value.SignificantNodes.Count).First().Value
            .SignificantNodes.Count;

        private Cluster GetOrAddCluster(Guid clusterId)
        {
            var cluster = _clusters.GetOrAdd(clusterId,
                guid => new Cluster
                {
                    ClusterId = guid, SignificantNodes = new ConcurrentBag<Node>(), AllNodes = new ConcurrentBag<Node>()
                });

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

        private Guid? GetClusterId(float[] encoding, double distance)
        {
            var guids = _guidLists.GetObject();
            guids.Clear();

            _significantNodesLock.EnterReadLock();

            foreach (var node in _allSignificantNodes)
                if (IsDistanceSmallerThan(encoding, node.Encoding, distance))
                    guids.Add(node.ClusterId);


            var clusterId = guids.GroupBy(x => x)
                    .OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;

            _significantNodesLock.ExitReadLock();

            _guidLists.PutObject(guids);

            return clusterId;
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

        private static Span<float> Min(Span<float> lhs, Span<float> rhs)
        {
            return BinaryVectorOperation(lhs, rhs, Vector.Min);
        }

        private static Span<float> Max(Span<float> lhs, Span<float> rhs)
        {
            return BinaryVectorOperation(lhs, rhs, Vector.Max);
        }

        private static Span<float> Subtract(Span<float> lhs, Span<float> rhs)
        {
            return BinaryVectorOperation(lhs, rhs, Vector.Subtract);
        }

        private static Span<float> Add(Span<float> lhs, Span<float> rhs)
        {
            return BinaryVectorOperation(lhs, rhs, Vector.Add);
        }

        private static Span<float> Multiply(Span<float> lhs, float value)
        {
            return UnaryVectorOperation(lhs, vector => Vector.Multiply(value, vector));
        }

        private static Span<float> BinaryVectorOperation(Span<float> lhs, Span<float> rhs,
            Func<Vector<float>, Vector<float>, Vector<float>> operation)
        {
            if (lhs.Length > rhs.Length || lhs.Length == 0)
                throw new ArgumentOutOfRangeException();

            if ((lhs.Length & (Vector<float>.Count - 1)) != 0)
                throw new ArgumentOutOfRangeException("Not a multiple of vector length");

            Span<float> result = new float[lhs.Length];
            ref var resultStart = ref result[0];

            for (var i = 0; i <= lhs.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                ref var part = ref Unsafe.As<float, Vector<float>>(ref Unsafe.Add(ref resultStart, i));

                part = operation(new Vector<float>(lhs.Slice(i, Vector<float>.Count)),
                    new Vector<float>(rhs.Slice(i, Vector<float>.Count)));
            }

            return result;
        }

        private static Span<float> UnaryVectorOperation(Span<float> lhs, Func<Vector<float>, Vector<float>> operation)
        {
            if (lhs.Length == 0)
                throw new ArgumentOutOfRangeException();

            if ((lhs.Length & (Vector<float>.Count - 1)) != 0)
                throw new ArgumentOutOfRangeException("Not a multiple of vector length");

            Span<float> result = new float[lhs.Length];
            ref var resultStart = ref result[0];

            for (var i = 0; i <= lhs.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                ref var part = ref Unsafe.As<float, Vector<float>>(ref Unsafe.Add(ref resultStart, i));
                part = operation(new Vector<float>(lhs.Slice(i, Vector<float>.Count)));
            }

            return result;
        }

        private void OptimizeCluster(Cluster cluster)
        {
            var nodeList = cluster.AllNodes;
            var distances = new ConcurrentDictionary<Node, double>();

            Span<float> min = _max;
            Span<float> max = _min;

            foreach (var node in nodeList)
            {
                max = Max(max, node.Encoding);
                min = Min(min, node.Encoding);
            }

            var distance = Subtract(max, min);
            var midway = Multiply(distance, 0.5f);

            var center = Add(min, midway);

            foreach (var node in nodeList)
                distances[node] = Distance(center, node.Encoding);

            var oldSignificantNodes = cluster.SignificantNodes;

            cluster.SignificantNodes = new ConcurrentBag<Node>(nodeList.Select((node, i) => (Node: node, Index: i))
                .OrderByDescending(x => distances[x.Node]).Take(150).Select(x => x.Node));
            cluster.AllNodes = cluster.SignificantNodes;

            _significantNodesLock.EnterWriteLock();

            foreach (var oldSignificantNode in oldSignificantNodes)
                _allSignificantNodes.Remove(oldSignificantNode);

            _allSignificantNodes.AddRange(cluster.SignificantNodes);

            _significantNodesLock.ExitWriteLock();
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
            public ConcurrentBag<Node> SignificantNodes { get; set; }

            public Guid ClusterId { get; set; }

            public ConcurrentBag<Node> AllNodes { get; set; }

            public ReaderWriterLockSlim Lock { get; } = new ReaderWriterLockSlim();
        }

        private class Node
        {
            public float[] Encoding { get; set; }

            public Guid ClusterId { get; set; }
        }
    }
}