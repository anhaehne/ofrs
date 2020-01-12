using System;
using System.Collections.Generic;
using System.Linq;
using DlibDotNet;

namespace Clustering.BenchmarkDotNet.Algorithms
{
    public class ChineseWhispersV1Dlib : IClusteringAlgorithm
    {
        private readonly double _clusterDistance;
        private readonly List<Node> _nodes = new List<Node>();

        public ChineseWhispersV1Dlib(double clusterDistance)
        {
            _clusterDistance = clusterDistance;
        }

        public Guid GetCluster(float[] encoding)
        {
            var dlibEncoding = Convert(encoding);

            var clusterId = _nodes.Where(x => Distance(dlibEncoding, x.Encoding) < _clusterDistance)
                .GroupBy(x => x.ClusterId)
                .OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;

            if (clusterId == null)
                clusterId = Guid.NewGuid();

            var newNode = new Node {ClusterId = clusterId.Value, Encoding = dlibEncoding};
            _nodes.Add(newNode);

            if (_nodes.Count % 300 == 0)
                Recalculate();

            return newNode.ClusterId;
        }

        public int ClusterCount => _nodes.Select(x => x.ClusterId).Distinct().Count();

        public int MaxClusterSize => _nodes.GroupBy(x => x.ClusterId).OrderByDescending(x => x.Count()).First().Count();

        private void Recalculate()
        {
            var edges = new List<SamplePair>();

            for (var i = 0; i < _nodes.Count; ++i)
            for (var j = i; j < _nodes.Count; ++j)
            {
                // Faces are connected in the graph if they are close enough.  Here we check if
                // the distance between two face descriptors is less than 0.6, which is the
                // decision threshold the network was trained to use.  Although you can
                // certainly use any other threshold you find useful.
                using var diff = _nodes[i].Encoding - _nodes[j].Encoding;

                if (Dlib.Length(diff) < 0.6)
                    edges.Add(new SamplePair((uint) i, (uint) j));
            }

            Dlib.ChineseWhispers(edges, 100, out var numClusters, out var labels);

            var newClusters = _nodes.Select((node, index) => (node, index)).GroupBy(x => labels[x.index]);

            foreach (var newCluster in newClusters)
            {
                var clusterId = newCluster.GroupBy(x => x.node.ClusterId).OrderByDescending(x => x.Count()).First().Key;

                foreach (var node in newCluster)
                    node.node.ClusterId = clusterId;
            }
        }

        private static Matrix<float> Convert(float[] encoding)
        {
            var arr = new Array2D<float>(128, 1);

            for (var i = 0; i < 128; i++)
                arr[i][0] = encoding[i];

            var matrix = new Matrix<float>(arr);

            return matrix;
        }

        private double Distance(Matrix<float> left, Matrix<float> right)
        {
            var length = Dlib.Length(left - right);

            return length;
        }

        private class Node
        {
            public Matrix<float> Encoding { get; set; }

            public Guid ClusterId { get; set; }
        }
    }
}